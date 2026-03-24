namespace SquadScout.Contracts.Messages;

public sealed record OutputChunkPayload
{
    public string Content { get; init; } = string.Empty;

    public bool IsError { get; init; }
}
