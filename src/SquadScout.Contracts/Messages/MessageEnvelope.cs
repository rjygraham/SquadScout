namespace SquadScout.Contracts.Messages;

/// <summary>
/// Carries a single sequenced message across the broker, mobile client, and replay pipeline.
/// Ordered broker replay frames are identified by <c>{ sessionId, generation, sequence }</c>, while
/// client-authored traffic can carry its own client-local sequence for dedupe and correlation.
/// </summary>
/// <typeparam name="TPayload">The strongly typed message payload.</typeparam>
public sealed record MessageEnvelope<TPayload>
{
    public int ContractVersion { get; init; } = SessionEnvelopeContract.CurrentVersion;

    public string ProjectId { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    /// <summary>
    /// Broker-minted ordered-state generation for this session. If the broker or PTY resets ordered
    /// state without minting a new session id, it must increment this value before emitting more
    /// broker frames. Client frames echo the latest broker generation they are acting against.
    /// </summary>
    public long Generation { get; init; } = SessionEnvelopeContract.InitialGeneration;

    public SessionMessageType MessageType { get; init; }

    public MessageDirection Direction { get; init; }

    /// <summary>
    /// Broker-owned monotonic sequence for replayable broker-to-client frames. Leave unset for
    /// client-authored traffic and for broker control frames that are intentionally outside replay.
    /// </summary>
    public long? Sequence { get; init; }

    /// <summary>
    /// Optional client-owned sequence for client-to-broker traffic. This value never participates in
    /// broker replay ordering and is only used for client-local dedupe and correlation.
    /// </summary>
    public long? ClientSequence { get; init; }

    /// <summary>
    /// Cumulative acknowledgement of the highest contiguous broker-owned sequence the client has
    /// applied within the current generation. This value resets when the generation changes.
    /// </summary>
    public long? AcknowledgedSequence { get; init; }

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public string MessageId { get; init; } = string.Empty;

    public string CorrelationId { get; init; } = string.Empty;

    public string? CausationId { get; init; }

    public TPayload Payload { get; init; } = default!;

    public override string ToString() =>
        $"MessageEnvelope<{typeof(TPayload).Name}> {{ ContractVersion = {ContractVersion}, ProjectId = {ProjectId}, SessionId = {SessionId}, Generation = {Generation}, MessageType = {MessageType}, Direction = {Direction}, Sequence = {FormatNullable(Sequence)}, ClientSequence = {FormatNullable(ClientSequence)}, AcknowledgedSequence = {FormatNullable(AcknowledgedSequence)}, TimestampUtc = {TimestampUtc:O}, MessageId = {MessageId}, CorrelationId = {CorrelationId}, CausationId = {CausationId ?? "<none>"}, PayloadType = {typeof(TPayload).Name} }}";

    private static string FormatNullable(long? value) => value?.ToString() ?? "<none>";
}
