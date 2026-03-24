namespace SquadScout.Contracts.Messages;

public sealed record InputChunkPayload
{
    public string Content { get; init; } = string.Empty;
}
