using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SquadScout.Broker.Projects;
using SquadScout.Broker.Pty;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Broker.Tests.TestDoubles;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Realtime;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class SessionRelayPipelineTests
{
    [Fact]
    public async Task StartAsyncLaunchesPtyRoutesInputAndPublishesReplayableOutput()
    {
        var harness = await CreateHarnessAsync();
        await using var relay = harness.CreateRelay();

        var session = await relay.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests",
            Arguments = ["--project", "broker"]
        });

        Assert.Equal(SessionState.Running, session.State);

        var startRequest = Assert.Single(harness.PtyHost.StartRequests);
        Assert.Equal(session.SessionId, startRequest.SessionId);
        Assert.Equal(session.ProjectId, startRequest.ProjectId);
        Assert.Equal(@"D:\GitHub\SquadScout-6", startRequest.WorkingDirectory);
        Assert.Equal(["--project", "broker"], startRequest.Arguments);

        var validation = await relay.RelayInputAsync(session.SessionId, CreateInputEnvelope(session, clientSequence: 1, "status --json\n"));
        Assert.Equal(SequenceValidationStatus.Accepted, validation.Status);
        var sessionGroup = SessionGroupName.Create(session.ProjectId, session.SessionId);
        Assert.Equal(sessionGroup, Assert.Single(harness.SessionGroupResolver.ResolvedEnvelopeGroups));

        var ptySession = harness.PtyHost.GetRequiredSession(session.SessionId);
        Assert.Equal(["status --json\n"], ptySession.WrittenInputs);

        ptySession.EnqueueOutput("ready", afterTicks: 1);
        ptySession.EnqueueExit(0, afterTicks: 2);
        ptySession.ReleaseAll();

        var published = await harness.RelayPublisher.WaitForEnvelopeCountAsync(3);
        Assert.Equal(sessionGroup, Assert.Single(harness.RelayPublisher.JoinedSessionGroups));
        Assert.Equal(sessionGroup, Assert.Single(harness.RelayPublisher.LeftSessionGroups));

        Assert.Collection(
            published.Take(3),
            message =>
            {
                Assert.Equal(1, message.Sequence);
                Assert.Equal(SessionMessageType.SessionLifecycle, message.MessageType);
                var payload = MockPtyHarnessFixture.DeserializePayload<SessionLifecyclePayload>(message);
                Assert.Equal(SessionState.Running, payload.State);
                Assert.Equal("pty-started", payload.Reason);
            },
            message =>
            {
                Assert.Equal(2, message.Sequence);
                Assert.Equal(SessionMessageType.Output, message.MessageType);
                var payload = MockPtyHarnessFixture.DeserializePayload<OutputChunkPayload>(message);
                Assert.Equal("ready", payload.Content);
            },
            message =>
            {
                Assert.Equal(3, message.Sequence);
                Assert.Equal(SessionMessageType.SessionLifecycle, message.MessageType);
                var payload = MockPtyHarnessFixture.DeserializePayload<SessionLifecyclePayload>(message);
                Assert.Equal(SessionState.Stopped, payload.State);
                Assert.Equal(0, payload.ExitCode);
                Assert.Equal("pty-exited", payload.Reason);
            });

        var replay = await harness.Orchestrator.ReplayAsync(session.SessionId, CreateReplayRequest(session, clientSequence: 2, fromSequenceInclusive: 1));

        Assert.Equal(1, replay.Payload.FromSequenceInclusive);
        Assert.Equal(3, replay.Payload.ToSequenceInclusive);
        Assert.False(replay.Payload.GapDetected);
        Assert.Collection(
            replay.Payload.Messages,
            message => Assert.Equal(SessionMessageType.SessionLifecycle, message.MessageType),
            message => Assert.Equal(SessionMessageType.Output, message.MessageType),
            message => Assert.Equal(SessionMessageType.SessionLifecycle, message.MessageType));

        var descriptor = await harness.Orchestrator.GetAsync(session.SessionId);
        Assert.NotNull(descriptor);
        Assert.Equal(SessionState.Stopped, descriptor!.State);
    }

    [Fact]
    public async Task RelayInputAsyncDoesNotWriteDuplicateClientMessagesTwice()
    {
        var harness = await CreateHarnessAsync();
        await using var relay = harness.CreateRelay();

        var session = await relay.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        var envelope = CreateInputEnvelope(session, clientSequence: 1, "help\n");

        var first = await relay.RelayInputAsync(session.SessionId, envelope);
        var duplicate = await relay.RelayInputAsync(session.SessionId, envelope with { MessageId = "client-input-duplicate" });

        var ptySession = harness.PtyHost.GetRequiredSession(session.SessionId);
        Assert.Equal(SequenceValidationStatus.Accepted, first.Status);
        Assert.Equal(SequenceValidationStatus.Duplicate, duplicate.Status);
        Assert.Equal(["help\n"], ptySession.WrittenInputs);
    }

    [Fact]
    public async Task StopAsyncSerializesWithAcceptedInputAndReturnsStructuredConflictForLaterInput()
    {
        var catalog = new InMemoryProjectCatalog();
        await catalog.UpsertAsync(new RegisteredProject
        {
            ProjectId = "broker",
            DisplayName = "Broker",
            RepositoryRoot = @"D:\GitHub\SquadScout-13"
        });

        var relayPublisher = new RecordingRelayPublisher();
        var orchestrator = new InMemorySessionOrchestrator(relayPublisher, new SessionSequenceValidator(), replayBufferCapacity: 8);
        var ptyHost = new GateablePtyHost();
        await using var relay = new InMemorySessionRelay(
            catalog,
            orchestrator,
            ptyHost,
            new PtySessionEnvelopePump(orchestrator),
            new SessionGroupResolver(),
            NullLogger<InMemorySessionRelay>.Instance);

        var session = await relay.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        var gateablePtySession = ptyHost.GetRequiredSession();
        var firstInput = relay.RelayInputAsync(session.SessionId, CreateInputEnvelope(session, clientSequence: 1, "before-stop\n"));
        await gateablePtySession.WaitForWriteEnteredAsync();

        var stopTask = relay.StopAsync(session.SessionId, new StopSessionCommand
        {
            ProjectId = session.ProjectId,
            RequestedBy = "tests",
            Reason = "user-requested-stop"
        });

        Assert.False(stopTask.IsCompleted);

        gateablePtySession.ReleaseWrite();

        var firstValidation = await firstInput;
        Assert.Equal(SequenceValidationStatus.Accepted, firstValidation.Status);

        await gateablePtySession.WaitForTerminateEnteredAsync();

        var exception = await Assert.ThrowsAsync<SessionControlException>(() => relay.RelayInputAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 2, "after-stop-accepted\n")));

        Assert.Equal("session_stop_in_progress", exception.Code);
        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal(session.SessionId, exception.SessionId);
        Assert.Equal(session.ProjectId, exception.ProjectId);
        Assert.Equal(SessionState.Running, exception.SessionState);
        Assert.Contains("no longer accepts input", exception.Message, StringComparison.OrdinalIgnoreCase);

        gateablePtySession.ReleaseTerminate();

        var stopped = await stopTask;
        Assert.Equal(SessionState.Stopped, stopped.State);
        Assert.Equal(["before-stop\n"], gateablePtySession.WrittenInputs);
    }

    [Fact]
    public async Task StopAsyncFailureKeepsStopRecoveryUnderSharedGateBeforeInputCanResume()
    {
        var catalog = new InMemoryProjectCatalog();
        await catalog.UpsertAsync(new RegisteredProject
        {
            ProjectId = "broker",
            DisplayName = "Broker",
            RepositoryRoot = @"D:\GitHub\SquadScout-13"
        });

        var relayPublisher = new RecordingRelayPublisher();
        var orchestrator = new InMemorySessionOrchestrator(relayPublisher, new SessionSequenceValidator(), replayBufferCapacity: 8);
        var ptyHost = new GateablePtyHost();
        await using var relay = new InMemorySessionRelay(
            catalog,
            orchestrator,
            ptyHost,
            new PtySessionEnvelopePump(orchestrator),
            new SessionGroupResolver(),
            NullLogger<InMemorySessionRelay>.Instance);

        var session = await relay.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        var gateablePtySession = ptyHost.GetRequiredSession();
        gateablePtySession.FailTerminate(new InvalidOperationException("terminate failed"));

        var stopTask = relay.StopAsync(session.SessionId, new StopSessionCommand
        {
            ProjectId = session.ProjectId,
            RequestedBy = "tests",
            Reason = "user-requested-stop"
        });

        await gateablePtySession.WaitForTerminateEnteredAsync();

        var stopInputGate = GetRequiredStopInputGate(relay, session.SessionId);
        await stopInputGate.WaitAsync();
        try
        {
            gateablePtySession.ReleaseTerminate();
            await gateablePtySession.WaitForTerminateFailureAsync();
            await Assert.ThrowsAsync<TimeoutException>(() => stopTask.WaitAsync(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            stopInputGate.Release();
        }

        var stopException = await Assert.ThrowsAsync<SessionControlException>(() => stopTask);
        Assert.Equal("session_stop_failed", stopException.Code);
        Assert.Equal(StatusCodes.Status500InternalServerError, stopException.StatusCode);
        Assert.Equal(session.SessionId, stopException.SessionId);
        Assert.Equal(session.ProjectId, stopException.ProjectId);
        Assert.Equal(SessionState.Running, stopException.SessionState);

        gateablePtySession.ReleaseWrite();
        var validation = await relay.RelayInputAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 1, "after-stop-failure\n"));

        Assert.Equal(SequenceValidationStatus.Accepted, validation.Status);
        Assert.Equal(["after-stop-failure\n"], gateablePtySession.WrittenInputs);
    }

    [Fact]
    public async Task RelayInputAsyncRejectsInactiveSessionsAfterPtyExit()
    {
        var harness = await CreateHarnessAsync();
        await using var relay = harness.CreateRelay();

        var session = await relay.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        var ptySession = harness.PtyHost.GetRequiredSession(session.SessionId);
        ptySession.EnqueueExit(0);
        ptySession.ReleaseNext();

        _ = await harness.RelayPublisher.WaitForEnvelopeCountAsync(2);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => relay.RelayInputAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 1, "after-exit\n")));

        Assert.Contains("active PTY", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopAsyncTerminatesActivePtyAndReturnsStoppedDescriptor()
    {
        var harness = await CreateHarnessAsync();
        await using var relay = harness.CreateRelay();

        var session = await relay.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        var stopped = await relay.StopAsync(session.SessionId, new StopSessionCommand
        {
            ProjectId = session.ProjectId,
            RequestedBy = "tests",
            Reason = "user-requested-stop"
        });

        Assert.Equal(SessionState.Stopped, stopped.State);

        var published = await harness.RelayPublisher.WaitForEnvelopeCountAsync(2);
        Assert.Collection(
            published.Take(2),
            message =>
            {
                Assert.Equal(SessionMessageType.SessionLifecycle, message.MessageType);
                var payload = MockPtyHarnessFixture.DeserializePayload<SessionLifecyclePayload>(message);
                Assert.Equal(SessionState.Running, payload.State);
                Assert.Equal("pty-started", payload.Reason);
            },
            message =>
            {
                Assert.Equal(SessionMessageType.SessionLifecycle, message.MessageType);
                var payload = MockPtyHarnessFixture.DeserializePayload<SessionLifecyclePayload>(message);
                Assert.Equal(SessionState.Stopped, payload.State);
                Assert.Equal("pty-exited", payload.Reason);
                Assert.Null(payload.ExitCode);
            });

        var descriptor = await harness.Orchestrator.GetAsync(session.SessionId);
        Assert.NotNull(descriptor);
        Assert.Equal(SessionState.Stopped, descriptor!.State);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => relay.RelayInputAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 1, "after-stop\n")));

        Assert.Contains("active PTY", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopAsyncRejectsProjectMismatchWithoutTerminatingSession()
    {
        var harness = await CreateHarnessAsync();
        await using var relay = harness.CreateRelay();

        var session = await relay.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        var exception = await Assert.ThrowsAsync<SessionControlException>(() => relay.StopAsync(
            session.SessionId,
            new StopSessionCommand
            {
                ProjectId = "other-project",
                RequestedBy = "tests"
            }));

        Assert.Equal("session_project_mismatch", exception.Code);
        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal(session.SessionId, exception.SessionId);
        Assert.Equal(session.ProjectId, exception.ProjectId);

        var validation = await relay.RelayInputAsync(session.SessionId, CreateInputEnvelope(session, clientSequence: 1, "still-running\n"));
        Assert.Equal(SequenceValidationStatus.Accepted, validation.Status);
    }

    [Fact]
    public async Task StopAsyncRejectsSessionsThatAlreadyExited()
    {
        var harness = await CreateHarnessAsync();
        await using var relay = harness.CreateRelay();

        var session = await relay.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        var ptySession = harness.PtyHost.GetRequiredSession(session.SessionId);
        ptySession.EnqueueExit(0);
        ptySession.ReleaseNext();
        _ = await harness.RelayPublisher.WaitForEnvelopeCountAsync(2);

        var exception = await Assert.ThrowsAsync<SessionControlException>(() => relay.StopAsync(
            session.SessionId,
            new StopSessionCommand
            {
                ProjectId = session.ProjectId,
                RequestedBy = "tests"
            }));

        Assert.Equal("session_already_stopped", exception.Code);
        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
    }

    [Fact]
    public async Task StartAsyncMarksSessionStoppedWhenPtyStartupFails()
    {
        var harness = await CreateHarnessAsync();
        harness.PtyHost.FailNextStart(new InvalidOperationException("boom"));
        await using var relay = harness.CreateRelay();

        await Assert.ThrowsAsync<InvalidOperationException>(() => relay.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        }));

        var startedSession = Assert.Single(harness.RelayPublisher.StartedSessions);
        var published = await harness.RelayPublisher.WaitForEnvelopeCountAsync(1);
        var lifecycle = Assert.Single(published);

        Assert.Equal(SessionMessageType.SessionLifecycle, lifecycle.MessageType);
        var payload = MockPtyHarnessFixture.DeserializePayload<SessionLifecyclePayload>(lifecycle);
        Assert.Equal(SessionState.Stopped, payload.State);
        Assert.Equal("pty-start-failed", payload.Reason);

        var descriptor = await harness.Orchestrator.GetAsync(startedSession.SessionId);
        Assert.NotNull(descriptor);
        Assert.Equal(SessionState.Stopped, descriptor!.State);
    }

    [Fact]
    public async Task StartAsyncRejectsUnknownProjectsBeforeCreatingSessions()
    {
        var harness = await CreateHarnessWithoutProjectsAsync();
        await using var relay = harness.CreateRelay();

        var exception = await Assert.ThrowsAsync<SessionControlException>(() => relay.StartAsync(new StartSessionCommand
        {
            ProjectId = "missing",
            RequestedBy = "tests"
        }));

        Assert.Equal("project_not_found", exception.Code);
        Assert.Equal(StatusCodes.Status404NotFound, exception.StatusCode);
        Assert.Empty(harness.RelayPublisher.StartedSessions);
    }

    private static async Task<RelayHarness> CreateHarnessAsync()
    {
        var catalog = new InMemoryProjectCatalog();
        await catalog.UpsertAsync(new RegisteredProject
        {
            ProjectId = "broker",
            DisplayName = "Broker",
            RepositoryRoot = @"D:\GitHub\SquadScout-6"
        });

        var relayPublisher = new RecordingRelayPublisher();
        var orchestrator = new InMemorySessionOrchestrator(relayPublisher, new SessionSequenceValidator(), replayBufferCapacity: 8);
        var ptyHost = new MockPtyHost();
        return new RelayHarness(catalog, relayPublisher, orchestrator, ptyHost, new RecordingSessionGroupResolver());
    }

    private static Task<RelayHarness> CreateHarnessWithoutProjectsAsync()
    {
        var catalog = new InMemoryProjectCatalog();
        var relayPublisher = new RecordingRelayPublisher();
        var orchestrator = new InMemorySessionOrchestrator(relayPublisher, new SessionSequenceValidator(), replayBufferCapacity: 8);
        var ptyHost = new MockPtyHost();
        return Task.FromResult(new RelayHarness(catalog, relayPublisher, orchestrator, ptyHost, new RecordingSessionGroupResolver()));
    }

    private static MessageEnvelope<InputChunkPayload> CreateInputEnvelope(
        SessionDescriptor session,
        long clientSequence,
        string content) =>
        new()
        {
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.Input,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = clientSequence,
            MessageId = $"client-input-{clientSequence}",
            CorrelationId = "corr-input",
            Payload = new InputChunkPayload
            {
                Content = content
            }
        };

    private static MessageEnvelope<ReplayRequestPayload> CreateReplayRequest(
        SessionDescriptor session,
        long clientSequence,
        long fromSequenceInclusive) =>
        new()
        {
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.ReplayRequest,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = clientSequence,
            MessageId = $"client-replay-{clientSequence}",
            CorrelationId = "corr-replay",
            Payload = new ReplayRequestPayload
            {
                FromSequenceInclusive = fromSequenceInclusive,
                MaximumMessages = 10,
                Reason = ReplayRequestReason.ReconnectResume
            }
        };

    private static SemaphoreSlim GetRequiredStopInputGate(InMemorySessionRelay relay, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(relay);

        var activeSession = GetRequiredActiveSession(relay, sessionId);
        return (SemaphoreSlim)(activeSession.GetType()
            .GetProperty("StopInputGate", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(activeSession)
            ?? throw new InvalidOperationException("Stop input gate was not available."));
    }

    private static object GetRequiredActiveSession(InMemorySessionRelay relay, string sessionId)
    {
        var activeSessionsField = typeof(InMemorySessionRelay).GetField(
            "_activeSessions",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Active relay sessions were not available.");

        var activeSessions = activeSessionsField.GetValue(relay)
            ?? throw new InvalidOperationException("Active relay sessions dictionary was not initialized.");

        var tryGetValue = activeSessions.GetType().GetMethod("TryGetValue")
            ?? throw new InvalidOperationException("Active relay session lookup was not available.");

        var arguments = new object?[] { sessionId, null };
        var found = (bool)(tryGetValue.Invoke(activeSessions, arguments)
            ?? throw new InvalidOperationException("Active relay session lookup returned no result."));

        return found
            ? arguments[1] ?? throw new InvalidOperationException("Active relay session was missing.")
            : throw new InvalidOperationException($"Active relay session '{sessionId}' was not found.");
    }

    private sealed class RelayHarness
    {
        public RelayHarness(
            InMemoryProjectCatalog projectCatalog,
            RecordingRelayPublisher relayPublisher,
            InMemorySessionOrchestrator orchestrator,
            MockPtyHost ptyHost,
            RecordingSessionGroupResolver sessionGroupResolver)
        {
            ProjectCatalog = projectCatalog;
            RelayPublisher = relayPublisher;
            Orchestrator = orchestrator;
            PtyHost = ptyHost;
            SessionGroupResolver = sessionGroupResolver;
        }

        public InMemoryProjectCatalog ProjectCatalog { get; }

        public RecordingRelayPublisher RelayPublisher { get; }

        public InMemorySessionOrchestrator Orchestrator { get; }

        public MockPtyHost PtyHost { get; }

        public RecordingSessionGroupResolver SessionGroupResolver { get; }

        public InMemorySessionRelay CreateRelay() =>
            new(
                ProjectCatalog,
                Orchestrator,
                PtyHost,
                new PtySessionEnvelopePump(Orchestrator),
                SessionGroupResolver,
                NullLogger<InMemorySessionRelay>.Instance);
    }

    private sealed class GateablePtyHost : IPtyHost
    {
        private GateablePtySession? _session;

        public Task<IPtySession> StartSessionAsync(PtySessionStartRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _session = new GateablePtySession(new MockPtySession(request));
            return Task.FromResult<IPtySession>(_session);
        }

        public GateablePtySession GetRequiredSession() =>
            _session ?? throw new InvalidOperationException("The gateable PTY session has not been created yet.");
    }

    private sealed class GateablePtySession : IPtySession
    {
        private readonly MockPtySession _inner;
        private readonly TaskCompletionSource _writeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _terminateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _terminateRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _terminateFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _terminateException;

        public GateablePtySession(MockPtySession inner)
        {
            _inner = inner;
        }

        public string SessionId => _inner.SessionId;

        public string ProjectId => _inner.ProjectId;

        public SessionState State => _inner.State;

        public IReadOnlyList<string> WrittenInputs => _inner.WrittenInputs;

        public Task WaitForWriteEnteredAsync() => _writeEntered.Task;

        public void ReleaseWrite() => _writeRelease.TrySetResult();

        public Task WaitForTerminateEnteredAsync() => _terminateEntered.Task;

        public Task WaitForTerminateFailureAsync() => _terminateFailure.Task;

        public void ReleaseTerminate() => _terminateRelease.TrySetResult();

        public void FailTerminate(Exception exception) => _terminateException = exception ?? throw new ArgumentNullException(nameof(exception));

        public async Task WriteAsync(string input, CancellationToken cancellationToken = default)
        {
            _writeEntered.TrySetResult();
            await _writeRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _inner.WriteAsync(input, cancellationToken).ConfigureAwait(false);
        }

        public bool TryReadEvent(out PtySessionEvent @event) => _inner.TryReadEvent(out @event);

        public ValueTask<PtySessionEvent> ReadEventAsync(CancellationToken cancellationToken = default) =>
            _inner.ReadEventAsync(cancellationToken);

        public async Task TerminateAsync(CancellationToken cancellationToken = default)
        {
            _terminateEntered.TrySetResult();
            await _terminateRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (_terminateException is not null)
            {
                _terminateFailure.TrySetResult();
                throw _terminateException;
            }

            await _inner.TerminateAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
