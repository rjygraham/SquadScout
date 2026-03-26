using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Realtime;

public interface ISessionGroupIngress
{
    Task StartAsync(SessionDescriptor session, CancellationToken cancellationToken = default);

    Task StopAsync(string sessionId, CancellationToken cancellationToken = default);
}
