using SquadScout.Contracts.Sessions;
using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Relay;

public sealed class NullRelayPublisher : IRelayPublisher
{
    public Task PublishSessionStartedAsync(SessionDescriptor session, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishEnvelopeAsync<TPayload>(MessageEnvelope<TPayload> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
