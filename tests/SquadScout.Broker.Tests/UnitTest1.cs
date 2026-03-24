using SquadScout.Broker.Projects;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class ScaffoldSmokeTests
{
    [Fact]
    public async Task ProjectCatalogStoresRegistrations()
    {
        var catalog = new InMemoryProjectCatalog();

        await catalog.UpsertAsync(new RegisteredProject
        {
            ProjectId = "broker",
            DisplayName = "Broker",
            RepositoryRoot = @"D:\GitHub\SquadScout"
        });

        var projects = await catalog.ListAsync();

        var project = Assert.Single(projects);
        Assert.Equal("broker", project.ProjectId);
    }

    [Fact]
    public async Task SessionOrchestratorReturnsPendingSessionDescriptors()
    {
        var orchestrator = new InMemorySessionOrchestrator(new NullRelayPublisher());

        var session = await orchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        Assert.Equal(SessionState.Pending, session.State);
        Assert.Equal(session, await orchestrator.GetAsync(session.SessionId));
    }
}
