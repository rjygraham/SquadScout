using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.Services;

public enum ProjectCatalogSource
{
    Broker,
    DevelopmentFallback
}

public enum SessionActivationSource
{
    None,
    Broker,
    DevelopmentFallback
}

public enum MessageConnectionState
{
    Disconnected,
    Ready,
    Connected
}

public sealed record ProjectCatalogSnapshot(
    IReadOnlyList<RegisteredProject> Projects,
    ProjectCatalogSource Source,
    string Summary);

public sealed record ClientIdentity(
    string RequestedBy,
    string DisplayName,
    string Mode);

public sealed record MessageConnectionStatus(
    MessageConnectionState State,
    string Summary,
    string Hub,
    bool SupportsLiveSessionStream);

public sealed record SessionLaunchResult(
    SessionDescriptor Session,
    SessionActivationSource Source,
    string Summary);

public sealed record ActiveSessionSnapshot(
    RegisteredProject? Project,
    SessionDescriptor? Session,
    SessionActivationSource Source,
    string Summary)
{
    public static ActiveSessionSnapshot Empty { get; } =
        new(null, null, SessionActivationSource.None, "No active session selected.");

    public bool HasActiveSession => Project is not null && Session is not null;
}

public interface IAuthenticationService
{
    Task<ClientIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken = default);
}

public interface IMessageConnectionService
{
    MessageConnectionStatus CurrentStatus { get; }

    Task<MessageConnectionStatus> PrepareForSessionAsync(SessionDescriptor session, CancellationToken cancellationToken = default);

    Task<MessageConnectionStatus> ResetAsync(CancellationToken cancellationToken = default);
}

public interface IProjectCatalogService
{
    Task<ProjectCatalogSnapshot> GetProjectsAsync(CancellationToken cancellationToken = default);
}

public interface ISessionLifecycleService
{
    Task<SessionLaunchResult> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default);

    Task<SessionDescriptor?> GetAsync(string sessionId, CancellationToken cancellationToken = default);
}

public interface IActiveSessionState
{
    event EventHandler<ActiveSessionSnapshot>? Changed;

    ActiveSessionSnapshot GetSnapshot();

    void SetActiveSession(RegisteredProject project, SessionDescriptor session, SessionActivationSource source, string summary);

    void UpdateSession(SessionDescriptor session, string? summary = null);

    void Clear(string summary = "No active session selected.");
}
