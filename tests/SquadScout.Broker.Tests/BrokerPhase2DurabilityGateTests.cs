using SquadScout.Broker.Configuration;
using SquadScout.Broker.Orleans;

namespace SquadScout.Broker.Tests;

public sealed class BrokerPhase2DurabilityGateTests
{
    [Fact]
    public void DurableSessionReplaySnapshotReportsPhase2GateMode()
    {
        var snapshot = OrleansHostStatusSnapshot.SessionGrains(
            CreateOptions(),
            CreateBootstrapResult("D:\\GitHub\\SquadScout-24\\.squadscout\\orleans\\phase2-session.db", schemaCreatedThisRun: true),
            CreateCompatibilityResult());

        Assert.True(snapshot.Enabled);
        Assert.Equal("session-grains", snapshot.HostMode);
        Assert.Equal("durable-grain", snapshot.SessionStateMode);
        Assert.Equal("in-memory", snapshot.ProjectStateMode);
        Assert.Contains("durable session replay state", snapshot.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("broker runtime path", snapshot.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DurableSessionProjectSnapshotReportsRestartBoundaryForGateReview()
    {
        var snapshot = OrleansHostStatusSnapshot.SessionProjectGrains(
            CreateOptions(),
            CreateBootstrapResult("D:\\GitHub\\SquadScout-24\\.squadscout\\orleans\\phase2-projects.db", schemaCreatedThisRun: false),
            CreateCompatibilityResult());

        Assert.True(snapshot.Enabled);
        Assert.Equal("session-project-grains", snapshot.HostMode);
        Assert.Equal("durable-grain", snapshot.SessionStateMode);
        Assert.Equal("durable-grain", snapshot.ProjectStateMode);
        Assert.Contains("project registration catalog", snapshot.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not revived", snapshot.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart active sessions", snapshot.Note, StringComparison.OrdinalIgnoreCase);
    }

    private static OrleansHostOptions CreateOptions() =>
        new()
        {
            Enabled = true,
            ClusterId = "test-cluster",
            ServiceId = "test-service",
            SiloPort = 11111,
            GatewayPort = 30000,
            StorageProvider = OrleansHostOptions.DefaultStorageProvider,
            AdoNetInvariant = OrleansHostOptions.DefaultAdoNetInvariant
        };

    private static OrleansSchemaBootstrapResult CreateBootstrapResult(string databasePath, bool schemaCreatedThisRun) =>
        new()
        {
            ConnectionString = "Data Source=.squadscout\\orleans\\tests.db;Mode=ReadWriteCreate;Cache=Shared",
            Invariant = OrleansHostOptions.DefaultAdoNetInvariant,
            DatabasePath = databasePath,
            SchemaReady = true,
            SchemaCreatedThisRun = schemaCreatedThisRun
        };

    private static OrleansSqliteCompatibilityResult CreateCompatibilityResult() =>
        new()
        {
            ConfiguredInvariant = OrleansHostOptions.DefaultAdoNetInvariant,
            Applied = true,
            Note = "SQLite compatibility shim applied."
        };
}
