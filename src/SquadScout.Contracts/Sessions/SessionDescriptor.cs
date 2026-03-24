namespace SquadScout.Contracts.Sessions;

public sealed record SessionDescriptor
{
    public string SessionId { get; init; } = string.Empty;

    public string ProjectId { get; init; } = string.Empty;

    public SessionState State { get; init; } = SessionState.Pending;

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
