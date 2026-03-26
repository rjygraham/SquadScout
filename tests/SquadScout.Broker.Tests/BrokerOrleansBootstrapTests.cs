using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SquadScout.Broker.Configuration;
using SquadScout.Broker.Orleans;

namespace SquadScout.Broker.Tests;

public sealed class BrokerOrleansBootstrapTests
{
    [Fact]
    public async Task OrleansStatusEndpointReportsDisabledInMemoryModeByDefault()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/orleans/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var status = await response.Content.ReadFromJsonAsync<OrleansHostStatusSnapshot>();

        Assert.NotNull(status);
        Assert.False(status!.Enabled);
        Assert.Equal("disabled", status.HostMode);
        Assert.Equal("in-memory", status.SessionStateMode);
        Assert.False(status.SchemaReady);
        Assert.Contains("Phase 1", status.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BootstrapOnlySnapshotKeepsSessionStateOnSafeInMemoryPath()
    {
        var options = new OrleansHostOptions
        {
            Enabled = true,
            ClusterId = "test-cluster",
            ServiceId = "test-service",
            SiloPort = 11111,
            GatewayPort = 30000,
            StorageProvider = "AdoNetStore",
            AdoNetInvariant = OrleansHostOptions.DefaultAdoNetInvariant
        };

        var snapshot = OrleansHostStatusSnapshot.BootstrapOnly(
            options,
            new OrleansSchemaBootstrapResult
            {
                ConnectionString = "Data Source=broker.db;Mode=ReadWriteCreate;Cache=Shared",
                Invariant = OrleansHostOptions.DefaultAdoNetInvariant,
                DatabasePath = "D:\\Broker\\broker.db",
                SchemaReady = true,
                SchemaCreatedThisRun = true
            },
            new OrleansSqliteCompatibilityResult
            {
                ConfiguredInvariant = OrleansHostOptions.DefaultAdoNetInvariant,
                Applied = true,
                Note = "Applied the local SQLite compatibility shim so Orleans ADO.NET storage recognizes Microsoft.Data.Sqlite for this single-silo broker."
            });

        Assert.True(snapshot.Enabled);
        Assert.Equal("bootstrap-only", snapshot.HostMode);
        Assert.Equal("in-memory", snapshot.SessionStateMode);
        Assert.True(snapshot.SchemaReady);
        Assert.True(snapshot.SchemaCreatedThisRun);
        Assert.True(snapshot.CompatibilityShimApplied);
        Assert.Contains("compatibility shim", snapshot.Note, StringComparison.OrdinalIgnoreCase);
    }
}
