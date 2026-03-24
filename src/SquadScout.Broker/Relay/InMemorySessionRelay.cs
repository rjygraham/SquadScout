using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;
using SquadScout.Broker.Projects;
using SquadScout.Broker.Pty;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Relay;

public sealed class InMemorySessionRelay : ISessionRelay, IAsyncDisposable
{
    private readonly IProjectCatalog _projectCatalog;
    private readonly ISessionOrchestrator _orchestrator;
    private readonly IPtyHost _ptyHost;
    private readonly PtySessionEnvelopePump _ptyPump;
    private readonly ILogger<InMemorySessionRelay> _logger;
    private readonly ConcurrentDictionary<string, ActiveRelaySession> _activeSessions = new(StringComparer.OrdinalIgnoreCase);
    private long _nextMessageId;

    public InMemorySessionRelay(
        IProjectCatalog projectCatalog,
        ISessionOrchestrator orchestrator,
        IPtyHost ptyHost,
        PtySessionEnvelopePump ptyPump,
        ILogger<InMemorySessionRelay> logger)
    {
        _projectCatalog = projectCatalog ?? throw new ArgumentNullException(nameof(projectCatalog));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _ptyHost = ptyHost ?? throw new ArgumentNullException(nameof(ptyHost));
        _ptyPump = ptyPump ?? throw new ArgumentNullException(nameof(ptyPump));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SessionDescriptor> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(command);

        var session = await _orchestrator.StartAsync(command, cancellationToken).ConfigureAwait(false);
        IPtySession? ptySession = null;

        try
        {
            var request = await CreateStartRequestAsync(session, command, cancellationToken).ConfigureAwait(false);
            ptySession = await _ptyHost.StartSessionAsync(request, cancellationToken).ConfigureAwait(false);

            await _ptyPump.PumpAvailableAsync(ptySession).ConfigureAwait(false);

            if (ptySession.State == SessionState.Stopped)
            {
                await ptySession.DisposeAsync().ConfigureAwait(false);
                return session with { State = SessionState.Stopped };
            }

            var activeSession = new ActiveRelaySession(ptySession);
            if (!_activeSessions.TryAdd(session.SessionId, activeSession))
            {
                throw new InvalidOperationException($"An active PTY session already exists for session '{session.SessionId}'.");
            }

            activeSession.PumpTask = RunPumpLoopAsync(session.SessionId, activeSession);
            return session with { State = SessionState.Running };
        }
        catch (OperationCanceledException)
        {
            if (ptySession is not null)
            {
                await SafeDisposeAsync(ptySession).ConfigureAwait(false);
            }

            await TryPublishLifecycleAsync(session.SessionId, SessionState.Stopped, "pty-start-cancelled").ConfigureAwait(false);
            throw;
        }
        catch
        {
            if (ptySession is not null)
            {
                await SafeDisposeAsync(ptySession).ConfigureAwait(false);
            }

            await TryPublishLifecycleAsync(session.SessionId, SessionState.Stopped, "pty-start-failed").ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SequenceValidationResult> RelayInputAsync(
        string sessionId,
        MessageEnvelope<InputChunkPayload> envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.Payload);

        if (envelope.MessageType != SessionMessageType.Input)
        {
            throw new ArgumentException("Relay input requires an input message envelope.", nameof(envelope));
        }

        if (envelope.Direction != MessageDirection.ClientToBroker)
        {
            throw new ArgumentException("Relay input requires a client-to-broker envelope.", nameof(envelope));
        }

        var activeSession = await GetRequiredActiveSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await _orchestrator.AcceptClientMessageAsync(
            sessionId,
            envelope,
            (acceptedEnvelope, token) => activeSession.PtySession.WriteAsync(acceptedEnvelope.Payload.Content, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        var activeSessions = _activeSessions.ToArray();
        _activeSessions.Clear();

        foreach (var (_, activeSession) in activeSessions)
        {
            try
            {
                await activeSession.PtySession.TerminateAsync().ConfigureAwait(false);
                await activeSession.PumpTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose relay session {SessionId}.", activeSession.PtySession.SessionId);
            }
        }
    }

    private async Task<PtySessionStartRequest> CreateStartRequestAsync(
        SessionDescriptor session,
        StartSessionCommand command,
        CancellationToken cancellationToken)
    {
        var project = await _projectCatalog.GetAsync(session.ProjectId, cancellationToken).ConfigureAwait(false);

        return new PtySessionStartRequest
        {
            SessionId = session.SessionId,
            ProjectId = session.ProjectId,
            WorkingDirectory = project?.RepositoryRoot ?? string.Empty,
            Arguments = command.Arguments
        };
    }

    private async Task<ActiveRelaySession> GetRequiredActiveSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A session id is required.", nameof(sessionId));
        }

        if (_activeSessions.TryGetValue(sessionId, out var activeSession))
        {
            return activeSession;
        }

        var session = await _orchestrator.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        }

        throw new InvalidOperationException($"Session '{sessionId}' does not have an active PTY session.");
    }

    private async Task RunPumpLoopAsync(string sessionId, ActiveRelaySession activeSession)
    {
        try
        {
            await _ptyPump.PumpUntilExitAsync(activeSession.PtySession).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PTY relay pump failed for session {SessionId}.", sessionId);

            try
            {
                await activeSession.PtySession.TerminateAsync().ConfigureAwait(false);
            }
            catch (Exception terminateEx)
            {
                _logger.LogWarning(terminateEx, "PTY termination failed after a relay pump fault for session {SessionId}.", sessionId);
            }

            if (activeSession.PtySession.State != SessionState.Stopped)
            {
                await TryPublishLifecycleAsync(sessionId, SessionState.Stopped, "relay-pump-failed").ConfigureAwait(false);
            }
        }
        finally
        {
            _activeSessions.TryRemove(sessionId, out _);
            await SafeDisposeAsync(activeSession.PtySession).ConfigureAwait(false);
        }
    }

    private async Task TryPublishLifecycleAsync(string sessionId, SessionState state, string reason)
    {
        try
        {
            await PublishLifecycleAsync(sessionId, state, reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish relay lifecycle {Reason} for session {SessionId}.", reason, sessionId);
        }
    }

    private Task PublishLifecycleAsync(string sessionId, SessionState state, string reason) =>
        _orchestrator.RecordBrokerMessageAsync(
            sessionId,
            new BrokerEnvelopeCommand<SessionLifecyclePayload>
            {
                MessageType = SessionMessageType.SessionLifecycle,
                MessageId = $"relay-{sessionId}-{Interlocked.Increment(ref _nextMessageId)}",
                CorrelationId = $"pty-session:{sessionId}",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new SessionLifecyclePayload
                {
                    State = state,
                    Reason = reason
                }
            });

    private async Task SafeDisposeAsync(IPtySession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PTY session disposal failed for session {SessionId}.", session.SessionId);
        }
    }

    private sealed class ActiveRelaySession
    {
        public ActiveRelaySession(IPtySession ptySession)
        {
            PtySession = ptySession;
        }

        public IPtySession PtySession { get; }

        public Task PumpTask { get; set; } = Task.CompletedTask;
    }
}
