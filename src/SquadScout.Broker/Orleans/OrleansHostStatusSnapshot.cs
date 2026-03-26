using SquadScout.Broker.Configuration;

namespace SquadScout.Broker.Orleans;

public sealed class OrleansHostStatusSnapshot
{
    public bool Enabled { get; init; }

    public string HostMode { get; init; } = "disabled";

    public string SessionStateMode { get; init; } = "in-memory";

    public string ClusterId { get; init; } = string.Empty;

    public string ServiceId { get; init; } = string.Empty;

    public int SiloPort { get; init; }

    public int GatewayPort { get; init; }

    public string StorageProvider { get; init; } = string.Empty;

    public string Invariant { get; init; } = string.Empty;

    public string? DatabasePath { get; init; }

    public bool SchemaReady { get; init; }

    public bool SchemaCreatedThisRun { get; init; }

    public bool CompatibilityShimApplied { get; init; }

    public string Summary { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;

    public static OrleansHostStatusSnapshot InMemory(OrleansHostOptions options) =>
        new()
        {
            Enabled = false,
            HostMode = "disabled",
            SessionStateMode = "in-memory",
            ClusterId = options.ClusterId,
            ServiceId = options.ServiceId,
            SiloPort = options.SiloPort,
            GatewayPort = options.GatewayPort,
            StorageProvider = options.StorageProvider,
            Invariant = options.AdoNetInvariant,
            SchemaReady = false,
            SchemaCreatedThisRun = false,
            CompatibilityShimApplied = false,
            Summary = "Orleans is disabled. Broker sessions and projects stay on the Phase 1 in-memory path.",
            Note = "Set Orleans:Enabled=true to start a local silo and bootstrap SQLite without switching session/project ownership yet."
        };

    public static OrleansHostStatusSnapshot BootstrapOnly(
        OrleansHostOptions options,
        OrleansSchemaBootstrapResult bootstrapResult,
        OrleansSqliteCompatibilityResult compatibilityResult) =>
        new()
        {
            Enabled = true,
            HostMode = "bootstrap-only",
            SessionStateMode = "in-memory",
            ClusterId = options.ClusterId,
            ServiceId = options.ServiceId,
            SiloPort = options.SiloPort,
            GatewayPort = options.GatewayPort,
            StorageProvider = options.StorageProvider,
            Invariant = bootstrapResult.Invariant,
            DatabasePath = bootstrapResult.DatabasePath,
            SchemaReady = bootstrapResult.SchemaReady,
            SchemaCreatedThisRun = bootstrapResult.SchemaCreatedThisRun,
            CompatibilityShimApplied = compatibilityResult.Applied,
            Summary = "Local Orleans silo is enabled with SQLite-backed ADO.NET storage. Session/project runtime ownership remains in-memory until the grain migration issues land.",
            Note = compatibilityResult.Note
        };
}
