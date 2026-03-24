namespace SquadScout.Contracts.Messages;

public sealed record ReplayRequestPayload
{
    public long FromSequenceInclusive { get; init; }

    public long? ToSequenceInclusive { get; init; }

    public int MaximumMessages { get; init; } = 100;

    public ReplayRequestReason Reason { get; init; } = ReplayRequestReason.GapDetected;
}
