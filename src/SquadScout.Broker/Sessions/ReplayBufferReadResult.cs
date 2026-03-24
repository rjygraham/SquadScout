using System.Text.Json;
using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Sessions;

public sealed record ReplayBufferReadResult
{
    public long? FromSequenceInclusive => Messages.Count == 0 ? null : Messages[0].Sequence;

    public long? ToSequenceInclusive => Messages.Count == 0 ? null : Messages[^1].Sequence;

    public long? AvailableFromSequence { get; init; }

    public long? AvailableToSequence { get; init; }

    public bool GapDetected { get; init; }

    public bool HasMore { get; init; }

    public bool IsComplete => !HasMore;

    public IReadOnlyList<MessageEnvelope<JsonElement>> Messages { get; init; } = Array.Empty<MessageEnvelope<JsonElement>>();
}
