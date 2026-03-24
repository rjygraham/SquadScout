namespace SquadScout.Broker.Sessions;

public sealed record SequenceValidationResult
{
    public SequenceValidationStatus Status { get; init; }

    public long Generation { get; init; }

    public long? ClientSequence { get; init; }

    public long? LastAcceptedClientSequence { get; init; }

    public long? ExpectedClientSequence { get; init; }

    public long? AppliedAcknowledgedSequence { get; init; }

    public string? Reason { get; init; }

    public bool IsAccepted => Status is SequenceValidationStatus.Accepted or SequenceValidationStatus.Duplicate;
}
