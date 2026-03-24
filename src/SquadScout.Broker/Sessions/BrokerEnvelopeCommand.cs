using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Sessions;

public sealed record BrokerEnvelopeCommand<TPayload>
{
    public SessionMessageType MessageType { get; init; }

    public string MessageId { get; init; } = Guid.NewGuid().ToString("n");

    public string CorrelationId { get; init; } = string.Empty;

    public string? CausationId { get; init; }

    public DateTimeOffset? TimestampUtc { get; init; }

    public TPayload Payload { get; init; } = default!;
}
