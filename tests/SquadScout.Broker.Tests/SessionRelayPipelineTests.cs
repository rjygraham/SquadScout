using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SquadScout.Broker.Projects;
using SquadScout.Broker.Pty;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Broker.Tests.TestDoubles;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Projects;
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

        var ptySession = harness.PtyHost.GetRequiredSession(session.SessionId);
        Assert.Equal(["status --json\n"], ptySession.WrittenInputs);

        ptySession.EnqueueOutput("ready", afterTicks: 1);
        ptySession.EnqueueExit(0, afterTicks: 2);
        ptySession.ReleaseAll();

        var published = await harness.RelayPublisher.WaitForEnvelopeCountAsync(3);

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
        return new RelayHarness(catalog, relayPublisher, orchestrator, ptyHost);
    }

    private static Task<RelayHarness> CreateHarnessWithoutProjectsAsync()
    {
        var catalog = new InMemoryProjectCatalog();
        var relayPublisher = new RecordingRelayPublisher();
        var orchestrator = new InMemorySessionOrchestrator(relayPublisher, new SessionSequenceValidator(), replayBufferCapacity: 8);
        var ptyHost = new MockPtyHost();
        return Task.FromResult(new RelayHarness(catalog, relayPublisher, orchestrator, ptyHost));
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

    private sealed class RelayHarness
    {
        public RelayHarness(
            InMemoryProjectCatalog projectCatalog,
            RecordingRelayPublisher relayPublisher,
            InMemorySessionOrchestrator orchestrator,
            MockPtyHost ptyHost)
        {
            ProjectCatalog = projectCatalog;
            RelayPublisher = relayPublisher;
            Orchestrator = orchestrator;
            PtyHost = ptyHost;
        }

        public InMemoryProjectCatalog ProjectCatalog { get; }

        public RecordingRelayPublisher RelayPublisher { get; }

        public InMemorySessionOrchestrator Orchestrator { get; }

        public MockPtyHost PtyHost { get; }

        public InMemorySessionRelay CreateRelay() =>
            new(
                ProjectCatalog,
                Orchestrator,
                PtyHost,
                new PtySessionEnvelopePump(Orchestrator),
                NullLogger<InMemorySessionRelay>.Instance);
    }
}
