using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Realtime;

public sealed class NullSessionGroupIngress : ISessionGroupIngress
{
    public Task StartAsync(SessionDescriptor session, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
