using Orleans;
using Orleans.Runtime;
using SquadScout.Broker.Configuration;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Orleans;

public sealed class SessionGrain : Grain, ISessionGrain
{
    private readonly IPersistentState<SessionGrainState> _persistentState;
    private readonly int _replayBufferCapacity;
    private readonly object _loadGateSync = new();
    private LoadGateState _loadGate = new();
    private SessionRuntimeState? _runtime;
    private volatile bool _isDeactivating;
    private volatile bool _stateLoaded;

    public SessionGrain(
        [PersistentState("session", OrleansHostOptions.DefaultStorageProvider)]
        IPersistentState<SessionGrainState> persistentState)
    {
        _persistentState = persistentState ?? throw new ArgumentNullException(nameof(persistentState));
        _replayBufferCapacity = SessionSequencingDefaults.ReplayBufferCapacity;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _isDeactivating = false;
        await EnsureRuntimeLoadedAsync().ConfigureAwait(true);
        await base.OnActivateAsync(cancellationToken).ConfigureAwait(true);
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _isDeactivating = true;
        _runtime = null;
        _stateLoaded = false;
        RetireLoadGate();
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    public async Task<SessionDescriptorRecord?> GetAsync()
    {
        await EnsureRuntimeLoadedAsync().ConfigureAwait(true);
        return _runtime?.Descriptor.ToRecord();
    }

    public async Task<SessionDescriptorRecord> StartAsync(SessionGrainStartCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        await EnsureRuntimeLoadedAsync().ConfigureAwait(true);

        if (_runtime is not null)
        {
            return _runtime.Descriptor.ToRecord();
        }

        if (string.IsNullOrWhiteSpace(command.ProjectId))
        {
            throw new ArgumentException("A project id is required.", nameof(command));
        }

        var descriptor = new SessionDescriptor
        {
            SessionId = command.SessionId,
            ProjectId = command.ProjectId,
            State = SessionState.Pending,
            CreatedAtUtc = command.CreatedAtUtc
        };

        _runtime = new SessionRuntimeState(descriptor, _replayBufferCapacity);
        _runtime.RecordSessionStarted(command.RequestedBy);
        await PersistAsync().ConfigureAwait(true);
        return descriptor.ToRecord();
    }

    public async Task<SessionEnvelopeRecord> RecordBrokerMessageAsync(BrokerEnvelopeCommandRecord command)
    {
        ArgumentNullException.ThrowIfNull(command);
        await EnsureRuntimeLoadedAsync().ConfigureAwait(true);

        var runtime = GetRequiredRuntime();
        var envelope = runtime.CreateBrokerEnvelope(command.ToJsonCommand());
        await PersistAsync().ConfigureAwait(true);
        return envelope.ToRecord();
    }

    public Task<SessionValidationRecord> ValidateClientMessageAsync(SessionEnvelopeRecord envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return ValidateCoreAsync(envelope);
    }

    public async Task CompleteClientMessageAsync(SessionValidationRecord result)
    {
        ArgumentNullException.ThrowIfNull(result);
        await EnsureRuntimeLoadedAsync().ConfigureAwait(true);

        var runtime = GetRequiredRuntime();
        runtime.ApplyValidationResult(result.ToValidationResult());
        await PersistAsync().ConfigureAwait(true);
    }

    public async Task RecordClientForwardFailureAsync(
        SessionEnvelopeRecord envelope,
        SessionValidationRecord result,
        string failureMessage)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(result);
        await EnsureRuntimeLoadedAsync().ConfigureAwait(true);

        var runtime = GetRequiredRuntime();
        runtime.RecordClientForwardFailure(
            envelope.ToJsonEnvelope(),
            result.ToValidationResult(),
            new InvalidOperationException(failureMessage));
        await PersistAsync().ConfigureAwait(true);
    }

    public async Task<SessionEnvelopeRecord> ReplayAsync(SessionEnvelopeRecord request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureRuntimeLoadedAsync().ConfigureAwait(true);

        var runtime = GetRequiredRuntime();
        var replayRequest = request.ToEnvelope<ReplayRequestPayload>();
        EnsureEnvelopeTargetsSession(replayRequest, runtime);

        runtime.RecordEnvelopeObserved(replayRequest);
        if (replayRequest.Generation == runtime.Generation)
        {
            var validation = new SessionSequenceValidator().Validate(runtime.CreateSnapshot(), replayRequest);
            runtime.RecordClientValidation(replayRequest, validation);
            if (!validation.IsAccepted)
            {
                throw new InvalidOperationException(validation.Reason ?? $"Replay request rejected with {validation.Status}.");
            }

            runtime.ApplyValidationResult(validation);
        }

        var response = runtime.CreateReplayResponse(replayRequest);
        await PersistAsync().ConfigureAwait(true);
        return new SessionEnvelopeRecord
        {
            ContractVersion = response.ContractVersion,
            ProjectId = response.ProjectId,
            SessionId = response.SessionId,
            Generation = response.Generation,
            MessageType = response.MessageType,
            Direction = response.Direction,
            Sequence = response.Sequence,
            ClientSequence = response.ClientSequence,
            AcknowledgedSequence = response.AcknowledgedSequence,
            TimestampUtc = response.TimestampUtc,
            MessageId = response.MessageId,
            CorrelationId = response.CorrelationId,
            CausationId = response.CausationId,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(ToRecord(response.Payload), SessionMessageSerializer.DefaultOptions)
        };
    }

    public Task<SessionTelemetrySnapshotRecord> ExportTelemetryAsync()
    {
        return ExportTelemetryCoreAsync();
    }

    public async Task<long> ResetGenerationAsync()
    {
        await EnsureRuntimeLoadedAsync().ConfigureAwait(true);
        var runtime = GetRequiredRuntime();
        var generation = runtime.ResetGeneration();
        await PersistAsync().ConfigureAwait(true);
        return generation;
    }

    private async Task PersistAsync()
    {
        if (_runtime is null)
        {
            _persistentState.State = new SessionGrainState();
            await _persistentState.ClearStateAsync().ConfigureAwait(true);
            return;
        }

        _persistentState.State = _runtime.ExportPersistenceSnapshot().ToGrainState();
        await _persistentState.WriteStateAsync().ConfigureAwait(true);
    }

    private async Task EnsureRuntimeLoadedAsync()
    {
        if (_stateLoaded || _isDeactivating)
        {
            return;
        }

        var loadGate = RentLoadGate();
        await loadGate.Semaphore.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_stateLoaded || _isDeactivating || loadGate.IsRetired)
            {
                return;
            }

            await _persistentState.ReadStateAsync().ConfigureAwait(true);
            _runtime = _persistentState.RecordExists
                ? _persistentState.State.ToRuntimeSnapshot() is { } snapshot
                    ? new SessionRuntimeState(snapshot, _replayBufferCapacity)
                    : null
                : null;
            _stateLoaded = true;
        }
        finally
        {
            loadGate.Semaphore.Release();
            ReleaseLoadGateLease(loadGate);
        }
    }

    private LoadGateState RentLoadGate()
    {
        lock (_loadGateSync)
        {
            _loadGate.LeaseCount++;
            return _loadGate;
        }
    }

    private void RetireLoadGate()
    {
        lock (_loadGateSync)
        {
            var retiredGate = _loadGate;
            retiredGate.IsRetired = true;
            _loadGate = new LoadGateState();
            TryDisposeRetiredLoadGate(retiredGate);
        }
    }

    private void ReleaseLoadGateLease(LoadGateState loadGate)
    {
        lock (_loadGateSync)
        {
            loadGate.LeaseCount--;
            TryDisposeRetiredLoadGate(loadGate);
        }
    }

    private static void TryDisposeRetiredLoadGate(LoadGateState loadGate)
    {
        if (!loadGate.IsRetired || loadGate.LeaseCount != 0)
        {
            return;
        }

        loadGate.Semaphore.Dispose();
    }

    private async Task<SessionValidationRecord> ValidateCoreAsync(SessionEnvelopeRecord envelope)
    {
        await EnsureRuntimeLoadedAsync().ConfigureAwait(true);

        var runtime = GetRequiredRuntime();
        var jsonEnvelope = envelope.ToJsonEnvelope();
        EnsureEnvelopeTargetsSession(jsonEnvelope, runtime);
        runtime.RecordEnvelopeObserved(jsonEnvelope);
        var result = new SessionSequenceValidator().Validate(runtime.CreateSnapshot(), jsonEnvelope);
        runtime.RecordClientValidation(jsonEnvelope, result);
        return result.ToRecord();
    }

    private async Task<SessionTelemetrySnapshotRecord> ExportTelemetryCoreAsync()
    {
        await EnsureRuntimeLoadedAsync().ConfigureAwait(true);
        var runtime = GetRequiredRuntime();
        return runtime.ExportTelemetry().ToRecord();
    }

    private SessionRuntimeState GetRequiredRuntime() =>
        _runtime ?? throw new KeyNotFoundException($"Session '{this.GetPrimaryKeyString()}' was not found.");

    private static ReplayResponsePayloadRecord ToRecord(ReplayResponsePayload payload) =>
        new()
        {
            Generation = payload.Generation,
            FromSequenceInclusive = payload.FromSequenceInclusive,
            ToSequenceInclusive = payload.ToSequenceInclusive,
            AvailableFromSequence = payload.AvailableFromSequence,
            AvailableToSequence = payload.AvailableToSequence,
            GapDetected = payload.GapDetected,
            HasMore = payload.HasMore,
            IsComplete = payload.IsComplete,
            Messages = payload.Messages.Select(message => message.ToRecord()).ToList()
        };

    private static void EnsureEnvelopeTargetsSession<TPayload>(MessageEnvelope<TPayload> envelope, SessionRuntimeState runtime)
    {
        if (!string.Equals(envelope.SessionId, runtime.Descriptor.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Envelope session id does not match the targeted session.", nameof(envelope));
        }

        if (!string.Equals(envelope.ProjectId, runtime.Descriptor.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Envelope project id does not match the targeted session.", nameof(envelope));
        }
    }

    private sealed class LoadGateState
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int LeaseCount { get; set; }

        public bool IsRetired { get; set; }
    }
}
