using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SquadScout.Broker.Relay;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Sessions;

public sealed class InMemorySessionOrchestrator : ISessionOrchestrator
{
    private readonly IRelayPublisher _relayPublisher;
    private readonly int _replayBufferCapacity;
    private readonly ISequenceValidator _sequenceValidator;
    private readonly ILogger<InMemorySessionOrchestrator> _logger;
    private readonly ConcurrentDictionary<string, SessionRuntimeState> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public InMemorySessionOrchestrator(
        IRelayPublisher relayPublisher,
        ISequenceValidator sequenceValidator,
        int replayBufferCapacity = SessionSequencingDefaults.ReplayBufferCapacity,
        ILogger<InMemorySessionOrchestrator>? logger = null)
    {
        _relayPublisher = relayPublisher;
        _sequenceValidator = sequenceValidator;
        _replayBufferCapacity = replayBufferCapacity;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemorySessionOrchestrator>.Instance;
    }

    public Task<SessionDescriptor?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.TryGetValue(sessionId, out var session);
        if (session is null)
        {
            return Task.FromResult<SessionDescriptor?>(null);
        }

        lock (session.SyncRoot)
        {
            return Task.FromResult<SessionDescriptor?>(session.Descriptor);
        }
    }

    public async Task<SessionDescriptor> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.ProjectId))
        {
            throw new ArgumentException("A project id is required.", nameof(command));
        }

        var session = new SessionDescriptor
        {
            SessionId = Guid.NewGuid().ToString("n"),
            ProjectId = command.ProjectId,
            State = SessionState.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var state = new SessionRuntimeState(session, _replayBufferCapacity);
        lock (state.SyncRoot)
        {
            state.RecordSessionStarted(command.RequestedBy);
        }

        _sessions[session.SessionId] = state;
        await _relayPublisher.PublishSessionStartedAsync(session, cancellationToken);

        return session;
    }

    public Task<MessageEnvelope<TPayload>> RecordBrokerMessageAsync<TPayload>(
        string sessionId,
        BrokerEnvelopeCommand<TPayload> command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetRequiredSessionState(sessionId);
        MessageEnvelope<TPayload> envelope;
        lock (state.SyncRoot)
        {
            envelope = state.CreateBrokerEnvelope(command);
        }

        return PublishBrokerEnvelopeAsync(envelope, cancellationToken);
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

        var state = GetRequiredSessionState(sessionId);
        EnsureEnvelopeTargetsSession(envelope, state);

        await state.ClientMessageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SequenceValidationResult result;
            lock (state.SyncRoot)
            {
                state.RecordEnvelopeObserved(envelope);
                result = _sequenceValidator.Validate(state.CreateSnapshot(), envelope);
                state.RecordClientValidation(envelope, result);
                if (!result.IsAccepted || result.Status == SequenceValidationStatus.Duplicate)
                {
                    state.ApplyValidationResult(result);
                    return result;
                }
            }

            try
            {
                await onAcceptedAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (state.SyncRoot)
                {
                    state.RecordClientForwardFailure(envelope, result, ex);
                }

                throw;
            }

            if (result.Status == SequenceValidationStatus.GapDetected)
            {
                _logger.LogWarning(
                    "Client sequence gap detected for project {ProjectId} session {SessionId} generation {Generation}: expected {Expected}, received {Received}, ack {AcknowledgedSequence}. MessageId={MessageId}, CorrelationId={CorrelationId}. Input forwarded.",
                    envelope.ProjectId,
                    envelope.SessionId,
                    result.Generation,
                    result.ExpectedClientSequence,
                    result.ClientSequence,
                    result.AppliedAcknowledgedSequence,
                    envelope.MessageId,
                    envelope.CorrelationId);
            }

            lock (state.SyncRoot)
            {
                state.ApplyValidationResult(result);
                return result;
            }
        }
        finally
        {
            state.ClientMessageGate.Release();
        }
    }

    public async Task<MessageEnvelope<ReplayResponsePayload>> ReplayAsync(
        string sessionId,
        MessageEnvelope<ReplayRequestPayload> request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        if (request.MessageType != SessionMessageType.ReplayRequest || request.Direction != MessageDirection.ClientToBroker)
        {
            throw new ArgumentException("Replay requests must be client-to-broker replay-request envelopes.", nameof(request));
        }

        var state = GetRequiredSessionState(sessionId);
        EnsureEnvelopeTargetsSession(request, state);

        await state.ClientMessageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MessageEnvelope<ReplayResponsePayload> response;
            lock (state.SyncRoot)
            {
                state.RecordEnvelopeObserved(request);
                if (request.Generation == state.Generation)
                {
                    var validation = _sequenceValidator.Validate(state.CreateSnapshot(), request);
                    state.RecordClientValidation(request, validation);
                    if (!validation.IsAccepted)
                    {
                        throw new InvalidOperationException(validation.Reason ?? $"Replay request rejected with {validation.Status}.");
                    }

                    state.ApplyValidationResult(validation);
                }

                response = state.CreateReplayResponse(request);
            }

            if (response.Payload.GapDetected)
            {
                _logger.LogWarning(
                    "Replay for session {SessionId} generation {Generation} reported a gap: requested {RequestedFrom}-{RequestedTo}, available {AvailableFrom}-{AvailableTo}, correlation {CorrelationId}.",
                    sessionId,
                    response.Generation,
                    request.Payload.FromSequenceInclusive,
                    request.Payload.ToSequenceInclusive,
                    response.Payload.AvailableFromSequence,
                    response.Payload.AvailableToSequence,
                    request.CorrelationId);
            }
            else
            {
                _logger.LogDebug(
                    "Replay for session {SessionId} generation {Generation} returned {MessageCount} message(s) for correlation {CorrelationId}.",
                    sessionId,
                    response.Generation,
                    response.Payload.Messages.Count,
                    request.CorrelationId);
            }

            return response;
        }
        finally
        {
            state.ClientMessageGate.Release();
        }
    }

    public Task<SessionTelemetrySnapshot> ExportTelemetryAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetRequiredSessionState(sessionId);
        lock (state.SyncRoot)
        {
            return Task.FromResult(state.ExportTelemetry());
        }
    }

    public Task<long> ResetGenerationAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetRequiredSessionState(sessionId);
        lock (state.SyncRoot)
        {
            return Task.FromResult(state.ResetGeneration());
        }
    }

    private SessionRuntimeState GetRequiredSessionState(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A session id is required.", nameof(sessionId));
        }

        if (_sessions.TryGetValue(sessionId, out var sessionState))
        {
            return sessionState;
        }

        throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
    }

    private static void EnsureEnvelopeTargetsSession<TPayload>(MessageEnvelope<TPayload> envelope, SessionRuntimeState state)
    {
        if (!string.Equals(envelope.SessionId, state.Descriptor.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Envelope session id does not match the targeted session.", nameof(envelope));
        }

        if (!string.Equals(envelope.ProjectId, state.Descriptor.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Envelope project id does not match the targeted session.", nameof(envelope));
        }
    }

    private async Task<MessageEnvelope<TPayload>> PublishBrokerEnvelopeAsync<TPayload>(
        MessageEnvelope<TPayload> envelope,
        CancellationToken cancellationToken)
    {
        await _relayPublisher.PublishEnvelopeAsync(envelope, cancellationToken);
        return envelope;
    }
}
