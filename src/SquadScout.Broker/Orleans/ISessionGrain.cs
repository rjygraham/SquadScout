using Orleans;

namespace SquadScout.Broker.Orleans;

public interface ISessionGrain : IGrainWithStringKey
{
    Task<SessionDescriptorRecord?> GetAsync();

    Task<SessionDescriptorRecord> StartAsync(SessionGrainStartCommand command);

    Task<SessionEnvelopeRecord> RecordBrokerMessageAsync(BrokerEnvelopeCommandRecord command);

    Task<SessionValidationRecord> ValidateClientMessageAsync(SessionEnvelopeRecord envelope);

    Task CompleteClientMessageAsync(SessionValidationRecord result);

    Task RecordClientForwardFailureAsync(SessionEnvelopeRecord envelope, SessionValidationRecord result, string failureMessage);

    Task<SessionEnvelopeRecord> ReplayAsync(SessionEnvelopeRecord request);

    Task<SessionTelemetrySnapshotRecord> ExportTelemetryAsync();

    Task<long> ResetGenerationAsync();
}
