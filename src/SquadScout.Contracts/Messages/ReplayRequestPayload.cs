namespace SquadScout.Contracts.Messages;

public sealed record ReplayRequestPayload
{
    /// <summary>
    /// First broker-owned sequence requested from the generation identified by the outer envelope.
    /// </summary>
    public long FromSequenceInclusive { get; init; }

    public long? ToSequenceInclusive { get; init; }

    public int MaximumMessages { get; init; } = 100;

    public ReplayRequestReason Reason { get; init; } = ReplayRequestReason.GapDetected;
}
