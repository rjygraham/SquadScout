using System.Text.Json;
using SquadScout.Contracts.Messages;
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
    Connecting,
    Ready,
    Connected,
    Reconnecting,
    Faulted
}

public sealed record ProjectCatalogSnapshot(
    IReadOnlyList<RegisteredProject> Projects,
    ProjectCatalogSource Source,
    string Summary);

public sealed record ClientIdentity(
    string RequestedBy,
    string DisplayName,
    string Mode);

public sealed record MessageConnectionStatus
{
    public MessageConnectionState State { get; init; } = MessageConnectionState.Disconnected;

    public string Summary { get; init; } = string.Empty;

    public string Hub { get; init; } = string.Empty;

    public bool SupportsLiveSessionStream { get; init; }

    public string? ProjectId { get; init; }

    public string? SessionId { get; init; }

    public string? SessionGroup { get; init; }

    public string? ConnectionId { get; init; }

    public DateTimeOffset? ConnectedAtUtc { get; init; }

    public DateTimeOffset? RefreshAtUtc { get; init; }

    public int ReconnectAttempt { get; init; }

    public string? FailureReason { get; init; }

    public long Generation { get; init; } = SessionEnvelopeContract.InitialGeneration;

    public long? AcknowledgedSequence { get; init; }

    public bool IsReplayPending { get; init; }

    public ReplayRequestReason? ReplayReason { get; init; }

    public long? ReplayFromSequenceInclusive { get; init; }

    public long? ReplayAvailableFromSequence { get; init; }

    public long? ReplayAvailableToSequence { get; init; }
}

public enum MessageTrafficDirection
{
    Incoming,
    Outgoing
}

public sealed record MessageEnvelopeTraffic
{
    public MessageTrafficDirection Direction { get; init; }

    public MessageEnvelope<JsonElement> Envelope { get; init; } = new();

    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Summary { get; init; } = string.Empty;
}

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

public sealed record MessageConnectionResumeState
{
    public long Generation { get; init; } = SessionEnvelopeContract.InitialGeneration;

    public long? AcknowledgedSequence { get; init; }
}

public sealed record ActiveSessionResumeState
{
    public ActiveSessionSnapshot Snapshot { get; init; } = ActiveSessionSnapshot.Empty;

    public MessageConnectionResumeState Connection { get; init; } = new();

    public IReadOnlyList<MessageEnvelopeTraffic> RecentTraffic { get; init; } = Array.Empty<MessageEnvelopeTraffic>();

    public DateTimeOffset SavedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public interface IAuthenticationService
{
    Task<ClientIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken = default);
}

public interface IMessageConnectionService
{
    event EventHandler<MessageConnectionStatus>? StatusChanged;

    event EventHandler<MessageEnvelopeTraffic>? TrafficObserved;

    MessageConnectionStatus CurrentStatus { get; }

    IReadOnlyList<MessageEnvelopeTraffic> RecentTraffic { get; }

    Task<MessageConnectionStatus> PrepareForSessionAsync(
        SessionDescriptor session,
        MessageConnectionResumeState? resumeState = null,
        CancellationToken cancellationToken = default);

    Task<MessageConnectionStatus> ReconnectAsync(CancellationToken cancellationToken = default);

    Task SendInputAsync(string content, CancellationToken cancellationToken = default);

    Task SendEnvelopeAsync<TPayload>(MessageEnvelope<TPayload> envelope, CancellationToken cancellationToken = default);

    Task<MessageConnectionStatus> ResetAsync(CancellationToken cancellationToken = default);
}

public interface ISessionResumeService
{
    ActiveSessionResumeState? CurrentState { get; }

    Task RestoreAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ActiveSessionResumeState state, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
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
