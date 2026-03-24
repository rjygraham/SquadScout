using SquadScout.Contracts.Sessions;

namespace SquadScout.Contracts.Messages;

public sealed record SessionLifecyclePayload
{
    public SessionState State { get; init; } = SessionState.Pending;

    public string Reason { get; init; } = string.Empty;

    public int? ExitCode { get; init; }
}
