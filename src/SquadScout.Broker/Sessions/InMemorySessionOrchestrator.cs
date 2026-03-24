using System.Collections.Concurrent;
using SquadScout.Broker.Relay;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Sessions;

public sealed class InMemorySessionOrchestrator : ISessionOrchestrator
{
    private readonly IRelayPublisher _relayPublisher;
    private readonly int _replayBufferCapacity;
    private readonly ISequenceValidator _sequenceValidator;
    private readonly ConcurrentDictionary<string, SessionRuntimeState> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public InMemorySessionOrchestrator(
        IRelayPublisher relayPublisher,
        ISequenceValidator sequenceValidator,
        int replayBufferCapacity = SessionSequencingDefaults.ReplayBufferCapacity)
    {
        _relayPublisher = relayPublisher;
        _sequenceValidator = sequenceValidator;
        _replayBufferCapacity = replayBufferCapacity;
    }

    public Task<SessionDescriptor?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session?.Descriptor);
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

        _sessions[session.SessionId] = new SessionRuntimeState(session, _replayBufferCapacity);
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
        lock (state.SyncRoot)
        {
            return Task.FromResult(state.CreateBrokerEnvelope(command));
        }
    }

    public Task<SequenceValidationResult> ValidateClientMessageAsync<TPayload>(
        string sessionId,
        MessageEnvelope<TPayload> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(envelope);

        var state = GetRequiredSessionState(sessionId);
        EnsureEnvelopeTargetsSession(envelope, state);
        lock (state.SyncRoot)
        {
            var result = _sequenceValidator.Validate(state.CreateSnapshot(), envelope);
            state.ApplyValidationResult(result);

            return Task.FromResult(result);
        }
    }

    public Task<MessageEnvelope<ReplayResponsePayload>> ReplayAsync(
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
        lock (state.SyncRoot)
        {
            if (request.Generation == state.Generation)
            {
                var validation = _sequenceValidator.Validate(state.CreateSnapshot(), request);
                if (!validation.IsAccepted)
                {
                    throw new InvalidOperationException(validation.Reason ?? $"Replay request rejected with {validation.Status}.");
                }

                state.ApplyValidationResult(validation);
            }

            return Task.FromResult(state.CreateReplayResponse(request));
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
}
