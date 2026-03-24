using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Relay;

public interface IRelayPublisher
{
    Task PublishSessionStartedAsync(SessionDescriptor session, CancellationToken cancellationToken = default);

    Task PublishEnvelopeAsync<TPayload>(MessageEnvelope<TPayload> envelope, CancellationToken cancellationToken = default);
}
