using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Sessions;

public interface ISessionOrchestrator
{
    Task<SessionDescriptor?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<SessionDescriptor> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default);

    Task<MessageEnvelope<TPayload>> RecordBrokerMessageAsync<TPayload>(
        string sessionId,
        BrokerEnvelopeCommand<TPayload> command,
        CancellationToken cancellationToken = default);

    Task<SequenceValidationResult> ValidateClientMessageAsync<TPayload>(
        string sessionId,
        MessageEnvelope<TPayload> envelope,
        CancellationToken cancellationToken = default);

    Task<SequenceValidationResult> AcceptClientMessageAsync<TPayload>(
        string sessionId,
        MessageEnvelope<TPayload> envelope,
        Func<MessageEnvelope<TPayload>, CancellationToken, Task> onAcceptedAsync,
        CancellationToken cancellationToken = default);

    Task<MessageEnvelope<ReplayResponsePayload>> ReplayAsync(
        string sessionId,
        MessageEnvelope<ReplayRequestPayload> request,
        CancellationToken cancellationToken = default);

    Task<SessionTelemetrySnapshot> ExportTelemetryAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<long> ResetGenerationAsync(string sessionId, CancellationToken cancellationToken = default);
}
