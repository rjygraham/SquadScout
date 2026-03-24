using System.Text.Json;

namespace SquadScout.Contracts.Messages;

public sealed record ReplayResponsePayload
{
    /// <summary>
    /// Ordered-state generation represented by this replay window. It must match the outer envelope
    /// generation. A mismatch from the client's last-seen generation indicates replay cannot resume
    /// the prior ordered stream and the client must treat it as a reset boundary.
    /// </summary>
    public long Generation { get; init; } = SessionEnvelopeContract.InitialGeneration;

    public long? FromSequenceInclusive { get; init; }

    public long? ToSequenceInclusive { get; init; }

    public long? AvailableFromSequence { get; init; }

    public long? AvailableToSequence { get; init; }

    public bool IsComplete { get; init; } = true;

    public bool HasMore { get; init; }

    public bool GapDetected { get; init; }

    public IReadOnlyList<MessageEnvelope<JsonElement>> Messages { get; init; } = Array.Empty<MessageEnvelope<JsonElement>>();
}
