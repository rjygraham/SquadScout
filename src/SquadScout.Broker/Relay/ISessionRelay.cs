using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Relay;

public interface ISessionRelay
{
    Task<SessionDescriptor> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default);

    Task<SessionDescriptor> StopAsync(string sessionId, StopSessionCommand command, CancellationToken cancellationToken = default);

    Task<SequenceValidationResult> RelayInputAsync(
        string sessionId,
        MessageEnvelope<InputChunkPayload> envelope,
        CancellationToken cancellationToken = default);
}
