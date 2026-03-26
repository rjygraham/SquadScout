using Microsoft.Data.Sqlite;
using SquadScout.Broker.Configuration;
using SquadScout.Broker.Orleans;

namespace SquadScout.Broker.Tests;

public sealed class OrleansSqliteSchemaBootstrapperTests
{
    [Fact]
    public async Task InitializeAsyncCreatesExpectedSchemaWhenDatabaseIsMissing()
    {
        var artifactRoot = CreateArtifactRoot();

        try
        {
            var databasePath = Path.Combine(artifactRoot, "bootstrap.db");
            var options = CreateOptions(databasePath);

            var result = await OrleansSqliteSchemaBootstrapper.InitializeAsync(options);

            Assert.True(result.SchemaReady);
            Assert.True(result.SchemaCreatedThisRun);
            Assert.Equal(Path.GetFullPath(databasePath), result.DatabasePath);
            Assert.True(File.Exists(databasePath));

            await using var connection = new SqliteConnection(result.ConnectionString);
            await connection.OpenAsync();

            Assert.True(await TableExistsAsync(connection, "OrleansQuery"));
            Assert.True(await TableExistsAsync(connection, "OrleansStorage"));
            Assert.True(await QueryExistsAsync(connection, "WriteToStorageKey"));
            Assert.True(await QueryExistsAsync(connection, "ReadFromStorageKey"));
            Assert.True(await QueryExistsAsync(connection, "ClearStorageKey"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task InitializeAsyncThrowsWhenSchemaIsMissingAndAutoInitializeIsDisabled()
    {
        var artifactRoot = CreateArtifactRoot();

        try
        {
            var databasePath = Path.Combine(artifactRoot, "missing.db");
            var options = CreateOptions(databasePath);
            options.AutoInitializeSchema = false;

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => OrleansSqliteSchemaBootstrapper.InitializeAsync(options));

            Assert.Contains("AutoInitializeSchema", exception.Message, StringComparison.Ordinal);

            if (File.Exists(databasePath))
            {
                await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
                await connection.OpenAsync();
                Assert.False(await TableExistsAsync(connection, "OrleansQuery"));
                Assert.False(await TableExistsAsync(connection, "OrleansStorage"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static OrleansHostOptions CreateOptions(string databasePath) =>
        new()
        {
            Enabled = true,
            SqliteConnectionString = $"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared"
        };

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> QueryExistsAsync(SqliteConnection connection, string queryKey)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM OrleansQuery WHERE QueryKey = $queryKey;";
        command.Parameters.AddWithValue("$queryKey", queryKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static string CreateArtifactRoot()
    {
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "orleans-bootstrap-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        return artifactRoot;
    }

}
