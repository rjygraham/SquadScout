using System.Net;
using SquadScout.App.Configuration;
using SquadScout.App.Services;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.Tests;

public sealed class MobileShellScaffoldTests
{
    [Fact]
    public void ActiveSessionStatePublishesRefreshedSnapshot()
    {
        var state = new ActiveSessionState();
        ActiveSessionSnapshot? observedSnapshot = null;

        state.Changed += (_, snapshot) => observedSnapshot = snapshot;

        state.SetActiveSession(
            new RegisteredProject
            {
                ProjectId = "squadscout",
                DisplayName = "SquadScout",
                RepositoryRoot = @"D:\GitHub\SquadScout-9"
            },
            new SessionDescriptor
            {
                SessionId = "session-1",
                ProjectId = "squadscout",
                State = SessionState.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow
            },
            SessionActivationSource.Broker,
            "Started");

        state.UpdateSession(
            new SessionDescriptor
            {
                SessionId = "session-1",
                ProjectId = "squadscout",
                State = SessionState.Running,
                CreatedAtUtc = DateTimeOffset.UtcNow
            },
            "Refreshed");

        Assert.NotNull(observedSnapshot);
        Assert.Equal(SessionState.Running, observedSnapshot!.Session!.State);
        Assert.Equal("Refreshed", observedSnapshot.Summary);
    }

    [Fact]
    public async Task ProjectCatalogFallsBackToDevelopmentSeedsWhenBrokerIsUnavailable()
    {
        using var httpClient = new HttpClient(new ThrowingHttpMessageHandler())
        {
            BaseAddress = new Uri("http://127.0.0.1:5071")
        };

        var service = new BrokerProjectCatalogService(
            httpClient,
            new AppEnvironment(AppEnvironment.DevelopmentName),
            new LocalDevelopmentOptions
            {
                UseSampleProjectsWhenBrokerUnavailable = true,
                SeedProjects =
                [
                    new SeedProjectOptions
                    {
                        ProjectId = "squadscout",
                        DisplayName = "SquadScout",
                        RepositoryRoot = @"D:\GitHub\SquadScout-9"
                    }
                ]
            });

        var snapshot = await service.GetProjectsAsync();

        Assert.Equal(ProjectCatalogSource.DevelopmentFallback, snapshot.Source);
        Assert.Single(snapshot.Projects);
        Assert.Contains("seed projects", snapshot.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionLifecycleCreatesDevelopmentPendingSessionWhenBrokerIsUnavailable()
    {
        using var httpClient = new HttpClient(new ThrowingHttpMessageHandler())
        {
            BaseAddress = new Uri("http://127.0.0.1:5071")
        };

        var service = new BrokerSessionLifecycleService(
            httpClient,
            new AppEnvironment(AppEnvironment.DevelopmentName),
            new LocalDevelopmentOptions
            {
                CreateOfflineSessionsWhenBrokerUnavailable = true
            });

        var result = await service.StartAsync(new StartSessionCommand
        {
            ProjectId = "squadscout",
            RequestedBy = "tests"
        });

        Assert.Equal(SessionActivationSource.DevelopmentFallback, result.Source);
        Assert.StartsWith("localdev-", result.Session.SessionId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SessionState.Pending, result.Session.State);
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("The broker is offline for this test.");
        }
    }
}
