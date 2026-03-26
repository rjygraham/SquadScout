namespace SquadScout.Contracts.Messages;

public sealed record SessionStatusRequestPayload
{
    public string SessionId { get; init; } = string.Empty;
}
