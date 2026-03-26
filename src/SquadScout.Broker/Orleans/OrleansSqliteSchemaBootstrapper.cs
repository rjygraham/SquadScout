using System.Reflection;
using Microsoft.Data.Sqlite;
using SquadScout.Broker.Configuration;

namespace SquadScout.Broker.Orleans;

public static class OrleansSqliteSchemaBootstrapper
{
    private static readonly string[] RequiredTables = ["OrleansQuery", "OrleansStorage"];
    private static readonly string[] RequiredQueryKeys = ["WriteToStorageKey", "ReadFromStorageKey", "ClearStorageKey"];

    public static async Task<OrleansSchemaBootstrapResult> InitializeAsync(
        OrleansHostOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var resolvedConnection = ResolveConnectionString(options.SqliteConnectionString);

        await using var connection = new SqliteConnection(resolvedConnection.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var schemaReady = await IsSchemaReadyAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!schemaReady)
        {
            if (!options.AutoInitializeSchema)
            {
                throw new InvalidOperationException(
                    "The Orleans SQLite schema is missing and Orleans:AutoInitializeSchema is disabled.");
            }

            await ExecuteEmbeddedScriptAsync(connection, "Sqlite-Main.sql", cancellationToken).ConfigureAwait(false);
            await ExecuteEmbeddedScriptAsync(connection, "Sqlite-Persistence.sql", cancellationToken).ConfigureAwait(false);
        }

        if (!await IsSchemaReadyAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Orleans SQLite bootstrap completed without creating the required Orleans schema.");
        }

        return new OrleansSchemaBootstrapResult
        {
            ConnectionString = resolvedConnection.ConnectionString,
            DatabasePath = resolvedConnection.DatabasePath,
            Invariant = options.AdoNetInvariant,
            SchemaReady = true,
            SchemaCreatedThisRun = !schemaReady
        };
    }

    private static async Task ExecuteEmbeddedScriptAsync(
        SqliteConnection connection,
        string scriptFileName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = await LoadEmbeddedScriptAsync(scriptFileName, cancellationToken).ConfigureAwait(false);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsSchemaReadyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var table in RequiredTables)
        {
            if (!await TableExistsAsync(connection, table, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        foreach (var queryKey in RequiredQueryKeys)
        {
            if (!await QueryExistsAsync(connection, queryKey, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result) > 0;
    }

    private static async Task<bool> QueryExistsAsync(
        SqliteConnection connection,
        string queryKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM OrleansQuery WHERE QueryKey = $queryKey;";
        command.Parameters.AddWithValue("$queryKey", queryKey);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result) > 0;
    }

    private static async Task<string> LoadEmbeddedScriptAsync(string scriptFileName, CancellationToken cancellationToken)
    {
        var assembly = typeof(OrleansSqliteSchemaBootstrapper).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(scriptFileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException($"Unable to locate embedded Orleans SQL script '{scriptFileName}'.");
        }

        await using var stream = assembly.GetManifestResourceStream(resourceName)
                                  ?? throw new InvalidOperationException($"Unable to load embedded Orleans SQL script '{resourceName}'.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ResolvedSqliteConnection ResolveConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Orleans:SqliteConnectionString must be configured when Orleans is enabled.", nameof(connectionString));
        }

        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new InvalidOperationException("Orleans:SqliteConnectionString must include a Data Source value.");
        }

        if (string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedSqliteConnection(builder.ToString(), builder.DataSource);
        }

        var databasePath = Path.GetFullPath(builder.DataSource);
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        builder.DataSource = databasePath;
        return new ResolvedSqliteConnection(builder.ToString(), databasePath);
    }

    private sealed record ResolvedSqliteConnection(string ConnectionString, string? DatabasePath);
}

public sealed class OrleansSchemaBootstrapResult
{
    public required string ConnectionString { get; init; }

    public required string Invariant { get; init; }

    public string? DatabasePath { get; init; }

    public bool SchemaReady { get; init; }

    public bool SchemaCreatedThisRun { get; init; }
}
