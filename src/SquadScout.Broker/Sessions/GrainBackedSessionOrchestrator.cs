using System.Collections.Concurrent;
using System.Text.Json;
using SquadScout.Broker.Orleans;
using SquadScout.Broker.Relay;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Sessions;

public sealed class GrainBackedSessionOrchestrator : ISessionOrchestrator
{
    private readonly IRelayPublisher _relayPublisher;
    private readonly ISessionGrainFactory _grainFactory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _clientMessageGates = new(StringComparer.OrdinalIgnoreCase);

    public GrainBackedSessionOrchestrator(IRelayPublisher relayPublisher, ISessionGrainFactory grainFactory)
    {
        _relayPublisher = relayPublisher ?? throw new ArgumentNullException(nameof(relayPublisher));
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
    }

    public async Task<SessionDescriptor?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var grain = GetRequiredSessionGrain(sessionId);
        var descriptor = await grain.GetAsync().ConfigureAwait(false);
        return descriptor?.ToDescriptor();
    }

    public async Task<SessionDescriptor> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(command.ProjectId))
        {
            throw new ArgumentException("A project id is required.", nameof(command));
        }

        var descriptor = new SessionDescriptor
        {
            SessionId = Guid.NewGuid().ToString("n"),
            ProjectId = command.ProjectId,
            State = SessionState.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var grain = GetRequiredSessionGrain(descriptor.SessionId);
        var started = (await grain.StartAsync(new SessionGrainStartCommand
        {
            SessionId = descriptor.SessionId,
            ProjectId = descriptor.ProjectId,
            CreatedAtUtc = descriptor.CreatedAtUtc,
            RequestedBy = command.RequestedBy
        }).ConfigureAwait(false)).ToDescriptor();

        await _relayPublisher.PublishSessionStartedAsync(started, cancellationToken).ConfigureAwait(false);
        return started;
    }

    public async Task<MessageEnvelope<TPayload>> RecordBrokerMessageAsync<TPayload>(
        string sessionId,
        BrokerEnvelopeCommand<TPayload> command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var record = await GetRequiredSessionGrain(sessionId)
            .RecordBrokerMessageAsync(command.ToRecord())
            .ConfigureAwait(false);

        var envelope = record.ToEnvelope<TPayload>();
        await _relayPublisher.PublishEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
        return envelope;
    }

    public Task<SequenceValidationResult> ValidateClientMessageAsync<TPayload>(
        string sessionId,
        MessageEnvelope<TPayload> envelope,
        CancellationToken cancellationToken = default) =>
        AcceptClientMessageAsync(
            sessionId,
            envelope,
            static (_, _) => Task.CompletedTask,
            cancellationToken);

    public async Task<SequenceValidationResult> AcceptClientMessageAsync<TPayload>(
        string sessionId,
        MessageEnvelope<TPayload> envelope,
        Func<MessageEnvelope<TPayload>, CancellationToken, Task> onAcceptedAsync,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(onAcceptedAsync);

        var gate = GetClientMessageGate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var grain = GetRequiredSessionGrain(sessionId);
            var validation = (await grain.ValidateClientMessageAsync(envelope.ToRecord()).ConfigureAwait(false)).ToValidationResult();
            if (!validation.IsAccepted || validation.Status == SequenceValidationStatus.Duplicate)
            {
                await grain.CompleteClientMessageAsync(validation.ToRecord()).ConfigureAwait(false);
                return validation;
            }

            try
            {
                await onAcceptedAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await grain.RecordClientForwardFailureAsync(
                        envelope.ToRecord(),
                        validation.ToRecord(),
                        ex.Message)
                    .ConfigureAwait(false);
                throw;
            }

            await grain.CompleteClientMessageAsync(validation.ToRecord()).ConfigureAwait(false);
            return validation;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<MessageEnvelope<ReplayResponsePayload>> ReplayAsync(
        string sessionId,
        MessageEnvelope<ReplayRequestPayload> request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        var gate = GetClientMessageGate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var responseRecord = await GetRequiredSessionGrain(sessionId)
                .ReplayAsync(request.ToRecord())
                .ConfigureAwait(false);

            var payloadRecord = JsonSerializer.Deserialize<ReplayResponsePayloadRecord>(
                responseRecord.PayloadJson,
                SessionMessageSerializer.DefaultOptions)
                ?? throw new InvalidOperationException("Unable to deserialize replay response payload.");

            return new MessageEnvelope<ReplayResponsePayload>
            {
                ContractVersion = responseRecord.ContractVersion,
                ProjectId = responseRecord.ProjectId,
                SessionId = responseRecord.SessionId,
                Generation = responseRecord.Generation,
                MessageType = responseRecord.MessageType,
                Direction = responseRecord.Direction,
                Sequence = responseRecord.Sequence,
                ClientSequence = responseRecord.ClientSequence,
                AcknowledgedSequence = responseRecord.AcknowledgedSequence,
                TimestampUtc = responseRecord.TimestampUtc,
                MessageId = responseRecord.MessageId,
                CorrelationId = responseRecord.CorrelationId,
                CausationId = responseRecord.CausationId,
                Payload = payloadRecord.ToPayload()
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SessionTelemetrySnapshot> ExportTelemetryAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var telemetry = await GetRequiredSessionGrain(sessionId).ExportTelemetryAsync().ConfigureAwait(false);
        return telemetry.ToSnapshot();
    }

    public Task<long> ResetGenerationAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return GetRequiredSessionGrain(sessionId).ResetGenerationAsync();
    }

    private ISessionGrain GetRequiredSessionGrain(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A session id is required.", nameof(sessionId));
        }

        return _grainFactory.GetGrain(sessionId);
    }

    private SemaphoreSlim GetClientMessageGate(string sessionId) =>
        _clientMessageGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
}
