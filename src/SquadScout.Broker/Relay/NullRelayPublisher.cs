using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Relay;

public sealed class NullRelayPublisher : IRelayPublisher
{
    public Task PublishSessionStartedAsync(SessionDescriptor session, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
