using System.Text.Json;

namespace SquadScout.Contracts.Messages;

public sealed record ReplayResponsePayload
{
    public long? FromSequenceInclusive { get; init; }

    public long? ToSequenceInclusive { get; init; }

    public long? AvailableFromSequence { get; init; }

    public long? AvailableToSequence { get; init; }

    public bool IsComplete { get; init; } = true;

    public bool HasMore { get; init; }

    public bool GapDetected { get; init; }

    public IReadOnlyList<MessageEnvelope<JsonElement>> Messages { get; init; } = Array.Empty<MessageEnvelope<JsonElement>>();
}
