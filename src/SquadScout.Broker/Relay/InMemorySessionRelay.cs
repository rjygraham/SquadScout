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

        var project = await GetRequiredProjectAsync(command.ProjectId, cancellationToken).ConfigureAwait(false);
        var session = await _orchestrator.StartAsync(command, cancellationToken).ConfigureAwait(false);
        IPtySession? ptySession = null;

        try
        {
            var request = CreateStartRequest(session, command, project);
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

    public async Task<SessionDescriptor> StopAsync(
        string sessionId,
        StopSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(command);

        var session = await GetRequiredSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        EnsureProjectMatches(command.ProjectId, session.ProjectId, session.SessionId, session.State);

        if (!_activeSessions.TryGetValue(session.SessionId, out var activeSession))
        {
            throw CreateInactiveStopException(session);
        }

        EnsureProjectMatches(command.ProjectId, activeSession.PtySession.ProjectId, session.SessionId, session.State);

        await activeSession.StopInputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!activeSession.TryBeginStop())
            {
                throw CreateStopInProgressException(
                    session.SessionId,
                    session.ProjectId,
                    session.State,
                    $"Session '{session.SessionId}' is already stopping for project '{session.ProjectId}'.");
            }
        }
        finally
        {
            activeSession.StopInputGate.Release();
        }

        try
        {
            await activeSession.PtySession.TerminateAsync().ConfigureAwait(false);
            await activeSession.PumpTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (activeSession.PtySession.State != SessionState.Stopped)
            {
                await ResetStopRequestAsync(activeSession).ConfigureAwait(false);
            }

            throw new SessionControlException(
                "session_stop_failed",
                StatusCodes.Status500InternalServerError,
                $"Stopping session '{session.SessionId}' for project '{session.ProjectId}' failed: {ex.Message}",
                session.SessionId,
                session.ProjectId,
                session.State,
                ex);
        }

        var stoppedSession = await _orchestrator.GetAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
        return stoppedSession ?? session with { State = SessionState.Stopped };
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

        await activeSession.StopInputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (activeSession.IsStopRequested)
            {
                throw CreateStopInProgressException(
                    activeSession.PtySession.SessionId,
                    activeSession.PtySession.ProjectId,
                    activeSession.PtySession.State,
                    $"Session '{sessionId}' is stopping and no longer accepts input.");
            }

            return await _orchestrator.AcceptClientMessageAsync(
                sessionId,
                envelope,
                (acceptedEnvelope, token) => activeSession.PtySession.WriteAsync(acceptedEnvelope.Payload.Content, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            activeSession.StopInputGate.Release();
        }
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
            finally
            {
                activeSession.Dispose();
            }
        }
    }

    private static PtySessionStartRequest CreateStartRequest(
        SessionDescriptor session,
        StartSessionCommand command,
        SquadScout.Contracts.Projects.RegisteredProject project)
    {
        return new PtySessionStartRequest
        {
            SessionId = session.SessionId,
            ProjectId = session.ProjectId,
            WorkingDirectory = project.RepositoryRoot,
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

    private async Task<SessionDescriptor> GetRequiredSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A session id is required.", nameof(sessionId));
        }

        var session = await _orchestrator.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is not null)
        {
            return session;
        }

        throw new SessionControlException(
            "session_not_found",
            StatusCodes.Status404NotFound,
            $"Session '{sessionId}' was not found.",
            sessionId);
    }

    private async Task<SquadScout.Contracts.Projects.RegisteredProject> GetRequiredProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("A project id is required.", nameof(projectId));
        }

        var project = await _projectCatalog.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is not null)
        {
            var repositoryRoot = project.RepositoryRoot?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                throw new SessionControlException(
                    "project_repository_root_missing",
                    StatusCodes.Status409Conflict,
                    $"Project '{project.ProjectId}' does not have a repository root configured for starting sessions.",
                    sessionId: $"pending:{project.ProjectId}",
                    projectId: project.ProjectId);
            }

            if (!Directory.Exists(repositoryRoot))
            {
                throw new SessionControlException(
                    "project_repository_root_not_found",
                    StatusCodes.Status409Conflict,
                    $"Project '{project.ProjectId}' points to repository root '{repositoryRoot}', but that path does not exist on this broker.",
                    sessionId: $"pending:{project.ProjectId}",
                    projectId: project.ProjectId);
            }

            return project with { RepositoryRoot = Path.GetFullPath(repositoryRoot) };
        }

        throw new SessionControlException(
            "project_not_found",
            StatusCodes.Status404NotFound,
            $"Project '{projectId}' is not registered with the broker.",
            sessionId: $"pending:{projectId}",
            projectId: projectId);
    }

    private static void EnsureProjectMatches(string requestedProjectId, string actualProjectId, string sessionId, SessionState sessionState)
    {
        if (string.IsNullOrWhiteSpace(requestedProjectId))
        {
            throw new ArgumentException("A project id is required.", nameof(requestedProjectId));
        }

        if (string.Equals(requestedProjectId, actualProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new SessionControlException(
            "session_project_mismatch",
            StatusCodes.Status409Conflict,
            $"Session '{sessionId}' belongs to project '{actualProjectId}', but the request targeted project '{requestedProjectId}'.",
            sessionId,
            actualProjectId,
            sessionState);
    }

    private static SessionControlException CreateInactiveStopException(SessionDescriptor session)
    {
        var (code, message) = session.State switch
        {
            SessionState.Stopped => (
                "session_already_stopped",
                $"Session '{session.SessionId}' is already stopped for project '{session.ProjectId}'."),
            SessionState.Pending => (
                "session_not_started",
                $"Session '{session.SessionId}' for project '{session.ProjectId}' has not finished starting, so there is no PTY session to stop yet."),
            _ => (
                "session_not_active",
                $"Session '{session.SessionId}' for project '{session.ProjectId}' does not currently have an active PTY session.")
        };

        return new SessionControlException(
            code,
            StatusCodes.Status409Conflict,
            message,
            session.SessionId,
            session.ProjectId,
            session.State);
    }

    private static SessionControlException CreateStopInProgressException(
        string sessionId,
        string projectId,
        SessionState sessionState,
        string message) =>
        new(
            "session_stop_in_progress",
            StatusCodes.Status409Conflict,
            message,
            sessionId,
            projectId,
            sessionState);

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
            activeSession.Dispose();
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

    private static async Task ResetStopRequestAsync(ActiveRelaySession activeSession)
    {
        await activeSession.StopInputGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            activeSession.ResetStopRequest();
        }
        finally
        {
            activeSession.StopInputGate.Release();
        }
    }

    private sealed class ActiveRelaySession : IDisposable
    {
        private int _disposed;
        private int _stopRequested;

        public ActiveRelaySession(IPtySession ptySession)
        {
            PtySession = ptySession;
        }

        public IPtySession PtySession { get; }

        public Task PumpTask { get; set; } = Task.CompletedTask;

        public SemaphoreSlim StopInputGate { get; } = new(1, 1);

        public bool IsStopRequested => Volatile.Read(ref _stopRequested) == 1;

        public bool TryBeginStop() => Interlocked.CompareExchange(ref _stopRequested, 1, 0) == 0;

        public void ResetStopRequest() => Interlocked.Exchange(ref _stopRequested, 0);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            StopInputGate.Dispose();
        }
    }
}
