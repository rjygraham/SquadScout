using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Sessions;

public interface ISessionOrchestrator
{
    Task<SessionDescriptor?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<SessionDescriptor> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default);
}
