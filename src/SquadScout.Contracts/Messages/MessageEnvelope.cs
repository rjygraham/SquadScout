namespace SquadScout.Contracts.Messages;

/// <summary>
/// Carries a single sequenced message across the broker, mobile client, and replay pipeline.
/// Sequence and acknowledgement values are session-scoped and remain the application-level source of truth.
/// </summary>
/// <typeparam name="TPayload">The strongly typed message payload.</typeparam>
public sealed record MessageEnvelope<TPayload>
{
    public int ContractVersion { get; init; } = SessionEnvelopeContract.CurrentVersion;

    public string ProjectId { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public SessionMessageType MessageType { get; init; }

    public MessageDirection Direction { get; init; }

    public long Sequence { get; init; }

    public long? AcknowledgedSequence { get; init; }

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public string MessageId { get; init; } = string.Empty;

    public string CorrelationId { get; init; } = string.Empty;

    public string? CausationId { get; init; }

    public TPayload Payload { get; init; } = default!;
}
