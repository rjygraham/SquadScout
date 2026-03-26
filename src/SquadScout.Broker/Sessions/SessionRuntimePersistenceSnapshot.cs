using System.Text.Json;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Sessions;

internal sealed record SessionRuntimePersistenceSnapshot(
    SessionDescriptor Descriptor,
    long Generation,
    long NextBrokerSequence,
    long? LastClientSequence,
    long? AcknowledgedSequence,
    IReadOnlyList<MessageEnvelope<JsonElement>> ReplayMessages,
    IReadOnlyList<SessionTelemetryEnvelope> RecentEnvelopes,
    IReadOnlyList<SessionTelemetryEvent> RecentEvents);
