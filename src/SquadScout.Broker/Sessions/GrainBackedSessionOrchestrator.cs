using System.Runtime.ExceptionServices;
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
    private readonly object _clientMessageGateSync = new();
    private readonly Dictionary<string, SessionClientMessageGate> _clientMessageGates = new(StringComparer.OrdinalIgnoreCase);

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
        try
        {
            await _relayPublisher.PublishEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (IsStoppedLifecycleEnvelope(envelope))
            {
                RetireClientMessageGate(sessionId);
            }
        }

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

        var gate = await AcquireClientMessageGateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            var grain = GetRequiredSessionGrain(sessionId);
            var validation = (await grain.ValidateClientMessageAsync(envelope.ToRecord()).ConfigureAwait(false)).ToValidationResult();
            if (!validation.IsAccepted || validation.Status == SequenceValidationStatus.Duplicate)
            {
                await grain.CompleteClientMessageAsync(validation.ToRecord()).ConfigureAwait(false);
                return validation;
            }

            ExceptionDispatchInfo? dispatchFailure = null;
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
                dispatchFailure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                await grain.CompleteClientMessageAsync(validation.ToRecord()).ConfigureAwait(false);
            }

            dispatchFailure?.Throw();

            return validation;
        }
        finally
        {
            ReleaseClientMessageGate(sessionId, gate);
        }
    }

    public async Task<MessageEnvelope<ReplayResponsePayload>> ReplayAsync(
        string sessionId,
        MessageEnvelope<ReplayRequestPayload> request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        var gate = await AcquireClientMessageGateAsync(sessionId, cancellationToken).ConfigureAwait(false);
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
            ReleaseClientMessageGate(sessionId, gate);
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

    private async Task<SessionClientMessageGate> AcquireClientMessageGateAsync(string sessionId, CancellationToken cancellationToken)
    {
        var gate = RentClientMessageGate(sessionId);
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return gate;
        }
        catch
        {
            ReleaseClientMessageGateLease(sessionId, gate);
            throw;
        }
    }

    private static bool IsStoppedLifecycleEnvelope<TPayload>(MessageEnvelope<TPayload> envelope) =>
        envelope.MessageType == SessionMessageType.SessionLifecycle
        && envelope.Payload is SessionLifecyclePayload lifecycle
        && lifecycle.State == SessionState.Stopped;

    private SessionClientMessageGate RentClientMessageGate(string sessionId)
    {
        lock (_clientMessageGateSync)
        {
            if (!_clientMessageGates.TryGetValue(sessionId, out var gate))
            {
                gate = new SessionClientMessageGate();
                _clientMessageGates[sessionId] = gate;
            }

            gate.LeaseCount++;
            return gate;
        }
    }

    private void RetireClientMessageGate(string sessionId)
    {
        lock (_clientMessageGateSync)
        {
            if (!_clientMessageGates.TryGetValue(sessionId, out var gate))
            {
                return;
            }

            gate.IsRetired = true;
            TryRemoveRetiredClientMessageGate(sessionId, gate);
        }
    }

    private void ReleaseClientMessageGate(string sessionId, SessionClientMessageGate gate)
    {
        gate.Semaphore.Release();
        ReleaseClientMessageGateLease(sessionId, gate);
    }

    private void ReleaseClientMessageGateLease(string sessionId, SessionClientMessageGate gate)
    {
        lock (_clientMessageGateSync)
        {
            gate.LeaseCount--;
            TryRemoveRetiredClientMessageGate(sessionId, gate);
        }
    }

    private void TryRemoveRetiredClientMessageGate(string sessionId, SessionClientMessageGate gate)
    {
        if (!gate.IsRetired ||
            gate.LeaseCount != 0 ||
            !_clientMessageGates.TryGetValue(sessionId, out var currentGate) ||
            !ReferenceEquals(currentGate, gate))
        {
            return;
        }

        _clientMessageGates.Remove(sessionId);
        gate.Semaphore.Dispose();
    }

    private sealed class SessionClientMessageGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int LeaseCount { get; set; }

        public bool IsRetired { get; set; }
    }
}
