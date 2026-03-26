-- Idempotent local bootstrap adaptation of the Orleans SQLite persistence schema.
-- Query keys use INSERT OR REPLACE so local startup can safely validate and re-run bootstrap.

CREATE TABLE IF NOT EXISTS OrleansStorage
(
    GrainIdHash              INT NOT NULL,
    GrainIdN0                BIGINT NOT NULL,
    GrainIdN1                BIGINT NOT NULL,
    GrainTypeHash            INT NOT NULL,
    GrainTypeString          NVARCHAR(512) NOT NULL,
    GrainIdExtensionString   NVARCHAR(512) NULL,
    ServiceId                NVARCHAR(150) NOT NULL,
    PayloadBinary            BLOB NULL,
    ModifiedOn               DATETIME NOT NULL,
    Version                  INT NULL
);

CREATE INDEX IF NOT EXISTS IX_OrleansStorage ON OrleansStorage(GrainIdHash, GrainTypeHash);

INSERT OR REPLACE INTO OrleansQuery (QueryKey, QueryText) VALUES
('WriteToStorageKey', '
    BEGIN TRANSACTION;

    CREATE TEMP TABLE IF NOT EXISTS OrleansStorageWriteState
    (
        TotalChangesBefore INT NOT NULL
    );
    DELETE FROM OrleansStorageWriteState;
    INSERT INTO OrleansStorageWriteState (TotalChangesBefore) VALUES (total_changes() + 1);

    UPDATE OrleansStorage
    SET
        PayloadBinary = @PayloadBinary,
        ModifiedOn = datetime(''now''),
        Version = Version + 1
    WHERE
        GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
        AND GrainTypeString = @GrainTypeString
        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
        AND ServiceId = @ServiceId
        AND Version = @GrainStateVersion;

    INSERT INTO OrleansStorage (GrainIdHash, GrainIdN0, GrainIdN1, GrainTypeHash, GrainTypeString, GrainIdExtensionString, ServiceId, PayloadBinary, ModifiedOn, Version)
    SELECT @GrainIdHash, @GrainIdN0, @GrainIdN1, @GrainTypeHash, @GrainTypeString, @GrainIdExtensionString, @ServiceId, @PayloadBinary, datetime(''now''), 1
    WHERE changes() = 0
      AND @GrainStateVersion IS NULL
      AND NOT EXISTS (
        SELECT 1 FROM OrleansStorage
        WHERE GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
        AND GrainTypeString = @GrainTypeString
        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
        AND ServiceId = @ServiceId
    );

    SELECT Version AS NewGrainStateVersion FROM OrleansStorage
    WHERE total_changes() > (SELECT TotalChangesBefore FROM OrleansStorageWriteState LIMIT 1)
        AND GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
        AND GrainTypeString = @GrainTypeString
        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
        AND ServiceId = @ServiceId;

    SELECT @GrainStateVersion AS NewGrainStateVersion
    WHERE total_changes() = (SELECT TotalChangesBefore FROM OrleansStorageWriteState LIMIT 1)
        AND @GrainStateVersion IS NOT NULL;

    COMMIT;
');

INSERT OR REPLACE INTO OrleansQuery (QueryKey, QueryText) VALUES
('ReadFromStorageKey', '
    SELECT
        PayloadBinary,
        Version AS Version
    FROM
        OrleansStorage
    WHERE
        GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
        AND GrainTypeString = @GrainTypeString
        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
        AND ServiceId = @ServiceId
    LIMIT 1;
');

INSERT OR REPLACE INTO OrleansQuery (QueryKey, QueryText) VALUES
('ClearStorageKey', '
    UPDATE OrleansStorage
    SET
        PayloadBinary = NULL,
        ModifiedOn = datetime(''now''),
        Version = Version + 1
    WHERE
        GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
        AND GrainTypeString = @GrainTypeString
        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
        AND ServiceId = @ServiceId
        AND Version = @GrainStateVersion;

    SELECT Version AS NewGrainStateVersion FROM OrleansStorage
    WHERE changes() > 0
        AND GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
        AND GrainTypeString = @GrainTypeString
        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
        AND ServiceId = @ServiceId;

    SELECT @GrainStateVersion AS NewGrainStateVersion
    WHERE changes() = 0
        AND @GrainStateVersion IS NOT NULL;
');
