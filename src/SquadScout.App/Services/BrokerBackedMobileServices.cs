using System.Net;
using System.Net.Http.Json;
using SquadScout.App.Configuration;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.Services;

public sealed class ConfiguredAuthenticationService : IAuthenticationService
{
    private readonly AuthOptions _authOptions;

    public ConfiguredAuthenticationService(AuthOptions authOptions)
    {
        _authOptions = authOptions;
    }

    public Task<ClientIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var requestedBy = string.IsNullOrWhiteSpace(_authOptions.DefaultRequestedBy)
            ? "mobile-user"
            : _authOptions.DefaultRequestedBy.Trim();

        return Task.FromResult(new ClientIdentity(
            requestedBy,
            requestedBy,
            string.IsNullOrWhiteSpace(_authOptions.Mode) ? "Configured" : _authOptions.Mode));
    }
}

public sealed class BrokerProjectCatalogService : IProjectCatalogService
{
    private readonly Func<HttpClient> _createHttpClient;
    private readonly AppEnvironment _environment;
    private readonly LocalDevelopmentOptions _localDevelopmentOptions;

    public BrokerProjectCatalogService(
        Func<HttpClient> createHttpClient,
        AppEnvironment environment,
        LocalDevelopmentOptions localDevelopmentOptions)
    {
        _createHttpClient = createHttpClient;
        _environment = environment;
        _localDevelopmentOptions = localDevelopmentOptions;
    }

    public async Task<ProjectCatalogSnapshot> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        using var httpClient = _createHttpClient();

        try
        {
            var projects = await httpClient.GetFromJsonAsync<RegisteredProject[]>("api/projects", cancellationToken)
                ?? Array.Empty<RegisteredProject>();

            if (projects.Length > 0 || !CanUseFallbackProjects())
            {
                return new ProjectCatalogSnapshot(
                    projects,
                    ProjectCatalogSource.Broker,
                    projects.Length == 0
                        ? "The broker is reachable, but no projects are registered yet."
                        : $"Loaded {projects.Length} project(s) from the broker.");
            }

            return CreateFallbackCatalog("The broker returned no projects, so local development seeds are shown.");
        }
        catch (Exception ex) when (CanUseFallbackProjects() && ex is HttpRequestException or TaskCanceledException)
        {
            return CreateFallbackCatalog("The broker is unavailable, so local development seed projects are shown.");
        }
    }

    private bool CanUseFallbackProjects() =>
        _environment.IsDevelopment &&
        _localDevelopmentOptions.UseSampleProjectsWhenBrokerUnavailable &&
        _localDevelopmentOptions.SeedProjects.Count > 0;

    private ProjectCatalogSnapshot CreateFallbackCatalog(string summary)
    {
        var projects = _localDevelopmentOptions.SeedProjects
            .Select(project => project.ToRegisteredProject())
            .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ProjectCatalogSnapshot(projects, ProjectCatalogSource.DevelopmentFallback, summary);
    }
}

public sealed class BrokerSessionLifecycleService : ISessionLifecycleService
{
    internal const string LocalDevelopmentSessionPrefix = "localdev-";

    private readonly Func<HttpClient> _createHttpClient;
    private readonly AppEnvironment _environment;
    private readonly LocalDevelopmentOptions _localDevelopmentOptions;

    public BrokerSessionLifecycleService(
        Func<HttpClient> createHttpClient,
        AppEnvironment environment,
        LocalDevelopmentOptions localDevelopmentOptions)
    {
        _createHttpClient = createHttpClient;
        _environment = environment;
        _localDevelopmentOptions = localDevelopmentOptions;
    }

    public async Task<SessionLaunchResult> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default)
    {
        using var httpClient = _createHttpClient();

        try
        {
            using var response = await httpClient.PostAsJsonAsync("api/sessions", command, cancellationToken);
            response.EnsureSuccessStatusCode();

            var session = await response.Content.ReadFromJsonAsync<SessionDescriptor>(cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("The broker did not return a session descriptor.");

            return new SessionLaunchResult(session, SessionActivationSource.Broker, "Started a broker-backed pending session.");
        }
        catch (Exception ex) when (CanCreateOfflineSession() && ex is HttpRequestException or TaskCanceledException)
        {
            var session = new SessionDescriptor
            {
                SessionId = $"{LocalDevelopmentSessionPrefix}{Guid.NewGuid():N}",
                ProjectId = command.ProjectId,
                State = SessionState.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            return new SessionLaunchResult(
                session,
                SessionActivationSource.DevelopmentFallback,
                "Created a local-development pending session scaffold because the broker is unavailable.");
        }
    }

    public async Task<SessionDescriptor?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            sessionId.StartsWith(LocalDevelopmentSessionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var httpClient = _createHttpClient();
        using var response = await httpClient.GetAsync($"api/sessions/{Uri.EscapeDataString(sessionId)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SessionDescriptor>(cancellationToken: cancellationToken);
    }

    private bool CanCreateOfflineSession() =>
        _environment.IsDevelopment &&
        _localDevelopmentOptions.CreateOfflineSessionsWhenBrokerUnavailable;
}

public sealed class ActiveSessionState : IActiveSessionState
{
    private readonly object _syncRoot = new();
    private ActiveSessionSnapshot _snapshot = ActiveSessionSnapshot.Empty;

    public event EventHandler<ActiveSessionSnapshot>? Changed;

    public ActiveSessionSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            return _snapshot;
        }
    }

    public void SetActiveSession(RegisteredProject project, SessionDescriptor session, SessionActivationSource source, string summary)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(session);

        Publish(new ActiveSessionSnapshot(project, session, source, summary));
    }

    public void UpdateSession(SessionDescriptor session, string? summary = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        ActiveSessionSnapshot? updatedSnapshot = null;

        lock (_syncRoot)
        {
            if (_snapshot.Project is null)
            {
                return;
            }

            updatedSnapshot = new ActiveSessionSnapshot(
                _snapshot.Project,
                session,
                _snapshot.Source,
                summary ?? _snapshot.Summary);

            _snapshot = updatedSnapshot;
        }

        Changed?.Invoke(this, updatedSnapshot);
    }

    public void Clear(string summary = "No active session selected.")
    {
        Publish(new ActiveSessionSnapshot(null, null, SessionActivationSource.None, summary));
    }

    private void Publish(ActiveSessionSnapshot snapshot)
    {
        lock (_syncRoot)
        {
            _snapshot = snapshot;
        }

        Changed?.Invoke(this, snapshot);
    }
}
