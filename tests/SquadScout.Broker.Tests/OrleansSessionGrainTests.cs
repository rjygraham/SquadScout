using System.Collections.Concurrent;
using System.Reflection;
using Orleans.Core;
using Orleans.Runtime;
using SquadScout.Broker.Orleans;
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

public sealed class OrleansSessionGrainTests
{
    [Fact]
    public async Task GrainBackedOrchestratorPersistsReplayBufferAndAcknowledgementAcrossReactivation()
    {
        var grainFactory = new TestSessionGrainFactory();
        var firstOrchestrator = new GrainBackedSessionOrchestrator(new NullRelayPublisher(), grainFactory);

        var session = await firstOrchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        var firstOutput = await firstOrchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Output,
                "broker-output-1",
                "corr-durable",
                new OutputChunkPayload { Content = "before restart" }));

        var accepted = await firstOrchestrator.AcceptClientMessageAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 1, acknowledgedSequence: 1, content: "status\n"),
            static (_, _) => Task.CompletedTask);

        var secondOrchestrator = new GrainBackedSessionOrchestrator(new NullRelayPublisher(), grainFactory);
        var persistedSession = await secondOrchestrator.GetAsync(session.SessionId);
        var secondOutput = await secondOrchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Output,
                "broker-output-2",
                "corr-durable",
                new OutputChunkPayload { Content = "after restart" }));

        var replay = await secondOrchestrator.ReplayAsync(
            session.SessionId,
            CreateReplayRequest(session, clientSequence: 2, generation: SessionEnvelopeContract.InitialGeneration, fromSequenceInclusive: 1));

        Assert.NotNull(persistedSession);
        Assert.Equal(1, firstOutput.Sequence);
        Assert.Equal(SequenceValidationStatus.Accepted, accepted.Status);
        Assert.Equal(2, secondOutput.Sequence);
        Assert.Equal(1, secondOutput.AcknowledgedSequence);
        Assert.Equal(1, replay.Payload.FromSequenceInclusive);
        Assert.Equal(2, replay.Payload.ToSequenceInclusive);
        Assert.False(replay.Payload.GapDetected);
        Assert.Collection(
            replay.Payload.Messages,
            message =>
            {
                Assert.Equal(1, message.Sequence);
                Assert.Null(message.AcknowledgedSequence);
            },
            message =>
            {
                Assert.Equal(2, message.Sequence);
                Assert.Equal(1, message.AcknowledgedSequence);
            });
    }

    [Fact]
    public async Task GrainBackedReplayDetectsOverflowAndSkipsHeartbeatControlFrames()
    {
        var grainFactory = new TestSessionGrainFactory();
        var orchestrator = new GrainBackedSessionOrchestrator(new NullRelayPublisher(), grainFactory);

        var session = await orchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        var firstOutput = await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Output,
                "broker-output-1",
                "corr-overflow",
                new OutputChunkPayload { Content = "one" }));

        var heartbeat = await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Heartbeat,
                "broker-heartbeat-1",
                "corr-overflow",
                new HeartbeatPayload { Nonce = "nonce-1" }));

        var secondOutput = await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Output,
                "broker-output-2",
                "corr-overflow",
                new OutputChunkPayload { Content = "two" }));

        var lifecycle = await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.SessionLifecycle,
                "broker-lifecycle-3",
                "corr-overflow",
                new SessionLifecyclePayload
                {
                    State = SessionState.Running,
                    Reason = "tests"
                }));

        MessageEnvelope<OutputChunkPayload>? lastOutput = null;
        var finalSequence = SessionSequencingDefaults.ReplayBufferCapacity + 1;
        for (var sequence = 4; sequence <= finalSequence; sequence++)
        {
            lastOutput = await orchestrator.RecordBrokerMessageAsync(
                session.SessionId,
                CreateBrokerCommand(
                    SessionMessageType.Output,
                    $"broker-output-{sequence}",
                    "corr-overflow",
                    new OutputChunkPayload { Content = $"output-{sequence}" }));
        }

        var replay = await orchestrator.ReplayAsync(
            session.SessionId,
            new MessageEnvelope<ReplayRequestPayload>
            {
                ProjectId = session.ProjectId,
                SessionId = session.SessionId,
                Generation = SessionEnvelopeContract.InitialGeneration,
                MessageType = SessionMessageType.ReplayRequest,
                Direction = MessageDirection.ClientToBroker,
                ClientSequence = 1,
                MessageId = "client-replay-overflow",
                CorrelationId = "corr-overflow",
                Payload = new ReplayRequestPayload
                {
                    FromSequenceInclusive = 1,
                    ToSequenceInclusive = finalSequence,
                    MaximumMessages = SessionSequencingDefaults.ReplayBufferCapacity + 1,
                    Reason = ReplayRequestReason.GapDetected
                }
            });

        Assert.Equal(1, firstOutput.Sequence);
        Assert.Null(heartbeat.Sequence);
        Assert.Equal(2, secondOutput.Sequence);
        Assert.Equal(3, lifecycle.Sequence);
        Assert.NotNull(lastOutput);
        Assert.Equal(finalSequence, lastOutput!.Sequence);
        Assert.True(replay.Payload.GapDetected);
        Assert.Equal(2, replay.Payload.FromSequenceInclusive);
        Assert.Equal(finalSequence, replay.Payload.ToSequenceInclusive);
        Assert.Equal(2, replay.Payload.AvailableFromSequence);
        Assert.Equal(finalSequence, replay.Payload.AvailableToSequence);
        Assert.Equal(SessionSequencingDefaults.ReplayBufferCapacity, replay.Payload.Messages.Count);
        Assert.Equal(2, replay.Payload.Messages[0].Sequence);
        Assert.Equal(3, replay.Payload.Messages[1].Sequence);
        Assert.Equal(finalSequence, replay.Payload.Messages[^1].Sequence);
        Assert.Equal(SessionMessageType.Output, replay.Payload.Messages[0].MessageType);
        Assert.Equal(SessionMessageType.SessionLifecycle, replay.Payload.Messages[1].MessageType);
        Assert.Equal(SessionMessageType.Output, replay.Payload.Messages[^1].MessageType);
        Assert.All(
            replay.Payload.Messages,
            message => Assert.True(
                message.MessageType is SessionMessageType.Output or SessionMessageType.SessionLifecycle));
        Assert.DoesNotContain(replay.Payload.Messages, message => message.MessageType == SessionMessageType.Heartbeat);
    }

    [Fact]
    public async Task GrainBackedReplayDetectsOverflowAndSkipsHeartbeatControlFramesWithSmallBuffer()
    {
        var grainFactory = new TestSessionGrainFactory(replayBufferCapacity: 3);
        var orchestrator = new GrainBackedSessionOrchestrator(new NullRelayPublisher(), grainFactory);
        var session = await orchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Output,
                "broker-output-1",
                "corr-output",
                new OutputChunkPayload { Content = "one" }));

        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Heartbeat,
                "broker-heartbeat-1",
                "corr-heartbeat",
                new HeartbeatPayload { Nonce = "nonce-1" }));

        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Output,
                "broker-output-2",
                "corr-output",
                new OutputChunkPayload { Content = "two" }));

        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.SessionLifecycle,
                "broker-lifecycle-3",
                "corr-lifecycle",
                new SessionLifecyclePayload { State = SessionState.Running }));

        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Output,
                "broker-output-4",
                "corr-output",
                new OutputChunkPayload { Content = "four" }));

        var replay = await orchestrator.ReplayAsync(
            session.SessionId,
            new MessageEnvelope<ReplayRequestPayload>
            {
                ProjectId = session.ProjectId,
                SessionId = session.SessionId,
                Generation = SessionEnvelopeContract.InitialGeneration,
                MessageType = SessionMessageType.ReplayRequest,
                Direction = MessageDirection.ClientToBroker,
                ClientSequence = 1,
                MessageId = "client-replay-1",
                CorrelationId = "corr-output",
                Payload = new ReplayRequestPayload
                {
                    FromSequenceInclusive = 1,
                    MaximumMessages = 10,
                    Reason = ReplayRequestReason.GapDetected
                }
            });

        Assert.Null(replay.Sequence);
        Assert.Equal(2, replay.Payload.FromSequenceInclusive);
        Assert.Equal(4, replay.Payload.ToSequenceInclusive);
        Assert.True(replay.Payload.GapDetected);
        Assert.Equal(2, replay.Payload.AvailableFromSequence);
        Assert.Equal(4, replay.Payload.AvailableToSequence);
        Assert.Equal("corr-output", replay.CorrelationId);
        Assert.Equal("client-replay-1", replay.CausationId);
        Assert.Collection(
            replay.Payload.Messages,
            message => Assert.Equal(2, message.Sequence),
            message => Assert.Equal(3, message.Sequence),
            message => Assert.Equal(4, message.Sequence));
    }

    [Fact]
    public async Task GrainBackedOrchestratorPersistsGenerationResetBoundaryAcrossReactivation()
    {
        var grainFactory = new TestSessionGrainFactory();
        var firstOrchestrator = new GrainBackedSessionOrchestrator(new NullRelayPublisher(), grainFactory);

        var session = await firstOrchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        await firstOrchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Output,
                "broker-output-before-reset",
                "corr-reset",
                new OutputChunkPayload { Content = "before reset" }));

        var activeGeneration = await firstOrchestrator.ResetGenerationAsync(session.SessionId);

        var postReset = await firstOrchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Output,
                "broker-output-after-reset",
                "corr-reset",
                new OutputChunkPayload { Content = "after reset" }));

        var secondOrchestrator = new GrainBackedSessionOrchestrator(new NullRelayPublisher(), grainFactory);
        var replay = await secondOrchestrator.ReplayAsync(
            session.SessionId,
            CreateReplayRequest(
                session,
                clientSequence: 1,
                generation: activeGeneration - 1,
                fromSequenceInclusive: 1,
                reason: ReplayRequestReason.ReconnectResume));

        Assert.Equal(1, postReset.Sequence);
        Assert.Equal(SessionEnvelopeContract.InitialGeneration + 1, activeGeneration);
        Assert.Equal(activeGeneration, replay.Generation);
        Assert.Equal(activeGeneration, replay.Payload.Generation);
        Assert.True(replay.Payload.GapDetected);
        Assert.Equal(1, replay.Payload.AvailableFromSequence);
        Assert.Equal(1, replay.Payload.AvailableToSequence);
        Assert.Empty(replay.Payload.Messages);
    }

    [Fact]
    public async Task GrainBackedOrchestratorClearsAcknowledgementBoundaryAcrossGenerationResetReactivation()
    {
        var grainFactory = new TestSessionGrainFactory();
        var firstOrchestrator = new GrainBackedSessionOrchestrator(new NullRelayPublisher(), grainFactory);

        var session = await firstOrchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        var beforeReset = await RecordOutputAsync(
            firstOrchestrator,
            session,
            "broker-output-before-ack-reset",
            "corr-ack-reset",
            "before reset");

        var acceptedBeforeReset = await firstOrchestrator.AcceptClientMessageAsync(
            session.SessionId,
            CreateInputEnvelope(
                session,
                clientSequence: 1,
                acknowledgedSequence: beforeReset.Sequence,
                content: "status\n"),
            static (_, _) => Task.CompletedTask);

        var nextGeneration = await firstOrchestrator.ResetGenerationAsync(session.SessionId);

        var secondOrchestrator = new GrainBackedSessionOrchestrator(new NullRelayPublisher(), grainFactory);
        var staleGeneration = await secondOrchestrator.ValidateClientMessageAsync(
            session.SessionId,
            CreateInputEnvelope(
                session,
                clientSequence: 2,
                acknowledgedSequence: beforeReset.Sequence,
                content: "stale\n",
                generation: SessionEnvelopeContract.InitialGeneration));

        var acceptedAfterReset = await secondOrchestrator.AcceptClientMessageAsync(
            session.SessionId,
            CreateInputEnvelope(
                session,
                clientSequence: 1,
                acknowledgedSequence: null,
                content: "fresh\n",
                generation: nextGeneration),
            static (_, _) => Task.CompletedTask);

        var afterReset = await RecordOutputAsync(
            secondOrchestrator,
            session,
            "broker-output-after-ack-reset",
            "corr-ack-reset",
            "after reset");

        var replay = await secondOrchestrator.ReplayAsync(
            session.SessionId,
            CreateReplayRequest(
                session,
                clientSequence: 2,
                generation: nextGeneration,
                fromSequenceInclusive: 1));

        Assert.Equal(SequenceValidationStatus.Accepted, acceptedBeforeReset.Status);
        Assert.Equal(beforeReset.Sequence, acceptedBeforeReset.AppliedAcknowledgedSequence);
        Assert.Equal(SequenceValidationStatus.StaleGeneration, staleGeneration.Status);
        Assert.Equal(SequenceValidationStatus.Accepted, acceptedAfterReset.Status);
        Assert.Equal(1, acceptedAfterReset.ClientSequence);
        Assert.Null(acceptedAfterReset.AppliedAcknowledgedSequence);
        Assert.Equal(nextGeneration, afterReset.Generation);
        Assert.Equal(1, afterReset.Sequence);
        Assert.Null(afterReset.AcknowledgedSequence);
        Assert.Collection(
            replay.Payload.Messages,
            message =>
            {
                Assert.Equal(1, message.Sequence);
                Assert.Equal(nextGeneration, message.Generation);
                Assert.Null(message.AcknowledgedSequence);
            });
    }

    [Fact]
    public async Task GrainBackedOrchestratorPersistsMultiPageReplayBoundariesAcrossMutationAndReactivation()
    {
        var grainFactory = new TestSessionGrainFactory();
        var firstOrchestrator = new GrainBackedSessionOrchestrator(new NullRelayPublisher(), grainFactory);

        var session = await firstOrchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        for (var sequence = 1; sequence <= 4; sequence++)
        {
            await RecordOutputAsync(
                firstOrchestrator,
                session,
                $"broker-output-page-{sequence}",
                "corr-page",
                $"message-{sequence}");
        }

        var firstPage = await firstOrchestrator.ReplayAsync(
            session.SessionId,
            CreateReplayRequest(
                session,
                clientSequence: 1,
                generation: SessionEnvelopeContract.InitialGeneration,
                fromSequenceInclusive: 1,
                maximumMessages: 2));

        var secondOrchestrator = new GrainBackedSessionOrchestrator(new NullRelayPublisher(), grainFactory);
        await RecordOutputAsync(
            secondOrchestrator,
            session,
            "broker-output-page-5",
            "corr-page",
            "message-5");

        var secondPage = await secondOrchestrator.ReplayAsync(
            session.SessionId,
            CreateReplayRequest(
                session,
                clientSequence: 2,
                generation: SessionEnvelopeContract.InitialGeneration,
                fromSequenceInclusive: 3,
                maximumMessages: 2));

        for (var sequence = 6; sequence <= 505; sequence++)
        {
            await RecordOutputAsync(
                secondOrchestrator,
                session,
                $"broker-output-overflow-{sequence}",
                "corr-page",
                $"overflow-{sequence}");
        }

        var thirdOrchestrator = new GrainBackedSessionOrchestrator(new NullRelayPublisher(), grainFactory);
        var overflowPage = await thirdOrchestrator.ReplayAsync(
            session.SessionId,
            CreateReplayRequest(
                session,
                clientSequence: 3,
                generation: SessionEnvelopeContract.InitialGeneration,
                fromSequenceInclusive: 1,
                maximumMessages: 2));

        var resumedPage = await thirdOrchestrator.ReplayAsync(
            session.SessionId,
            CreateReplayRequest(
                session,
                clientSequence: 4,
                generation: SessionEnvelopeContract.InitialGeneration,
                fromSequenceInclusive: 8,
                maximumMessages: 2));

        Assert.False(firstPage.Payload.GapDetected);
        Assert.True(firstPage.Payload.HasMore);
        Assert.Collection(
            firstPage.Payload.Messages,
            message => Assert.Equal(1, message.Sequence),
            message => Assert.Equal(2, message.Sequence));

        Assert.False(secondPage.Payload.GapDetected);
        Assert.True(secondPage.Payload.HasMore);
        Assert.Collection(
            secondPage.Payload.Messages,
            message => Assert.Equal(3, message.Sequence),
            message => Assert.Equal(4, message.Sequence));

        Assert.True(overflowPage.Payload.GapDetected);
        Assert.Equal(6, overflowPage.Payload.AvailableFromSequence);
        Assert.Equal(505, overflowPage.Payload.AvailableToSequence);
        Assert.True(overflowPage.Payload.HasMore);
        Assert.Collection(
            overflowPage.Payload.Messages,
            message => Assert.Equal(6, message.Sequence),
            message => Assert.Equal(7, message.Sequence));

        Assert.False(resumedPage.Payload.GapDetected);
        Assert.True(resumedPage.Payload.HasMore);
        Assert.Collection(
            resumedPage.Payload.Messages,
            message => Assert.Equal(8, message.Sequence),
            message => Assert.Equal(9, message.Sequence));
    }

    [Fact]
    public async Task GrainBackedOrchestratorCompletesValidationAfterForwardFailure()
    {
        var orchestrator = new GrainBackedSessionOrchestrator(new NullRelayPublisher(), new TestSessionGrainFactory());
        var session = await orchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.AcceptClientMessageAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 1, acknowledgedSequence: null, content: "status\n"),
            static (_, _) => throw new InvalidOperationException("forward failed")));

        var duplicate = await orchestrator.ValidateClientMessageAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 1, acknowledgedSequence: null, content: "status\n"));

        var accepted = await orchestrator.ValidateClientMessageAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 2, acknowledgedSequence: null, content: "retry\n"));

        Assert.Equal(SequenceValidationStatus.Duplicate, duplicate.Status);
        Assert.Equal(1, duplicate.LastAcceptedClientSequence);
        Assert.Equal(SequenceValidationStatus.Accepted, accepted.Status);
        Assert.Equal(2, accepted.ClientSequence);
        Assert.Equal(1, accepted.LastAcceptedClientSequence);
    }

    [Fact]
    public async Task GrainBackedOrchestratorRemovesClientGateWhenSessionStops()
    {
        var relayPublisher = new RecordingRelayPublisher();
        var orchestrator = new GrainBackedSessionOrchestrator(relayPublisher, new TestSessionGrainFactory());
        var session = await orchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        await orchestrator.ValidateClientMessageAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 1, acknowledgedSequence: null, content: "status\n"));

        Assert.Equal(1, GetClientMessageGateCount(orchestrator));

        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.SessionLifecycle,
                "relay-stopped",
                "corr-stopped",
                new SessionLifecyclePayload
                {
                    State = SessionState.Stopped,
                    Reason = "tests"
                }));

        Assert.Equal(0, GetClientMessageGateCount(orchestrator));
        Assert.Contains(
            relayPublisher.LeftSessionGroups,
            groupName => string.Equals(groupName, SessionGroupName.Create(session.ProjectId, session.SessionId), StringComparison.Ordinal));
    }

    [Fact]
    public async Task GrainBackedOrchestratorKeepsSerializingMessagesWhileStopRetiresGate()
    {
        var orchestrator = new GrainBackedSessionOrchestrator(new NullRelayPublisher(), new TestSessionGrainFactory());
        var session = await orchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        var firstAcceptedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstMessage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAccepted = orchestrator.AcceptClientMessageAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 1, acknowledgedSequence: null, content: "first\n"),
            async (_, _) =>
            {
                firstAcceptedEntered.SetResult();
                await releaseFirstMessage.Task.ConfigureAwait(true);
            });

        await firstAcceptedEntered.Task;

        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.SessionLifecycle,
                "relay-stopped-concurrent",
                "corr-stopped-concurrent",
                new SessionLifecyclePayload
                {
                    State = SessionState.Stopped,
                    Reason = "tests"
                }));

        var secondAccepted = orchestrator.ValidateClientMessageAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 2, acknowledgedSequence: null, content: "second\n"));

        await Task.Delay(50);
        Assert.False(secondAccepted.IsCompleted);

        releaseFirstMessage.SetResult();

        var firstResult = await firstAccepted;
        var secondResult = await secondAccepted;

        Assert.Equal(SequenceValidationStatus.Accepted, firstResult.Status);
        Assert.Equal(SequenceValidationStatus.Accepted, secondResult.Status);
        Assert.Equal(0, GetClientMessageGateCount(orchestrator));
    }

    [Fact]
    public async Task SessionGrainConcurrentLoadReadsPersistentStateOnce()
    {
        var persistentState = new TestPersistentState
        {
            ReadDelay = TimeSpan.FromMilliseconds(50)
        };
        var grain = new SessionGrain(persistentState);

        await Task.WhenAll(grain.GetAsync(), grain.GetAsync());

        Assert.Equal(1, persistentState.ReadStateCallCount);
    }

    [Fact]
    public async Task RelayPipelineRemainsCompatibleWithGrainBackedSessionState()
    {
        var phase1ProjectCatalog = new InMemoryProjectCatalog();
        await phase1ProjectCatalog.UpsertAsync(new RegisteredProject
        {
            ProjectId = "broker",
            DisplayName = "Broker",
            RepositoryRoot = GetRepositoryRoot()
        });
        var projectCatalog = new GrainBackedProjectCatalog(
            new TestProjectGrainFactory(),
            new TestProjectRegistryGrainFactory(),
            phase1ProjectCatalog);

        var relayPublisher = new RecordingRelayPublisher();
        var orchestrator = new GrainBackedSessionOrchestrator(relayPublisher, new TestSessionGrainFactory());
        var ptyHost = new MockPtyHost();
        await using var relay = new InMemorySessionRelay(
            projectCatalog,
            orchestrator,
            ptyHost,
            new PtySessionEnvelopePump(orchestrator),
            new SessionLivenessManager(TimeProvider.System, senderInstanceId: "broker-tests"),
            new SessionGroupResolver(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemorySessionRelay>.Instance);

        var session = await relay.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests",
            Arguments = ["--project", "broker"]
        });

        var validation = await relay.RelayInputAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 1, acknowledgedSequence: null, content: "help\n"));

        var ptySession = ptyHost.GetRequiredSession(session.SessionId);
        ptySession.EnqueueOutput("ready", afterTicks: 1);
        ptySession.EnqueueExit(0, afterTicks: 2);
        ptySession.ReleaseAll();

        var published = await relayPublisher.WaitForEnvelopeCountAsync(3);
        var replay = await orchestrator.ReplayAsync(
            session.SessionId,
            CreateReplayRequest(session, clientSequence: 2, generation: SessionEnvelopeContract.InitialGeneration, fromSequenceInclusive: 1));

        Assert.Equal(SequenceValidationStatus.Accepted, validation.Status);
        Assert.Equal(["help\n"], ptySession.WrittenInputs);
        Assert.Collection(
            published.Take(3),
            message => Assert.Equal(SessionMessageType.SessionLifecycle, message.MessageType),
            message => Assert.Equal(SessionMessageType.Output, message.MessageType),
            message => Assert.Equal(SessionMessageType.SessionLifecycle, message.MessageType));
        Assert.Collection(
            replay.Payload.Messages,
            message => Assert.Equal(SessionMessageType.SessionLifecycle, message.MessageType),
            message => Assert.Equal(SessionMessageType.Output, message.MessageType),
            message => Assert.Equal(SessionMessageType.SessionLifecycle, message.MessageType));
    }

    [Fact]
    public async Task GrainBackedProjectCatalogImportsPhase1ProjectsAndPersistsAcrossReactivation()
    {
        var phase1Catalog = new InMemoryProjectCatalog();
        await phase1Catalog.UpsertAsync(new RegisteredProject
        {
            ProjectId = "broker",
            DisplayName = "Broker",
            RepositoryRoot = GetRepositoryRoot()
        });

        var projectGrainFactory = new TestProjectGrainFactory();
        var projectRegistryGrainFactory = new TestProjectRegistryGrainFactory();
        var firstCatalog = new GrainBackedProjectCatalog(projectGrainFactory, projectRegistryGrainFactory, phase1Catalog);

        var importedProject = await firstCatalog.GetAsync("broker");
        await firstCatalog.UpsertAsync(new RegisteredProject
        {
            ProjectId = "mobile",
            DisplayName = "Mobile",
            RepositoryRoot = GetRepositoryRoot()
        });

        var secondCatalog = new GrainBackedProjectCatalog(
            projectGrainFactory,
            projectRegistryGrainFactory,
            new InMemoryProjectCatalog());
        var persistedProjects = await secondCatalog.ListAsync();

        Assert.NotNull(importedProject);
        Assert.Equal("broker", importedProject!.ProjectId);
        Assert.Collection(
            persistedProjects.OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase),
            project => Assert.Equal("broker", project.ProjectId),
            project => Assert.Equal("mobile", project.ProjectId));
    }

    [Fact]
    public async Task GrainBackedProjectCatalogPreservesPhase1SeedBoundaryAcrossReactivation()
    {
        var initialPhase1Catalog = new InMemoryProjectCatalog();
        await initialPhase1Catalog.UpsertAsync(new RegisteredProject
        {
            ProjectId = "alpha",
            DisplayName = "Alpha",
            RepositoryRoot = GetRepositoryRoot()
        });

        var projectGrainFactory = new TestProjectGrainFactory();
        var projectRegistryGrainFactory = new TestProjectRegistryGrainFactory();
        var firstCatalog = new GrainBackedProjectCatalog(projectGrainFactory, projectRegistryGrainFactory, initialPhase1Catalog);

        var imported = await firstCatalog.ListAsync();

        var restartedPhase1Catalog = new InMemoryProjectCatalog();
        await restartedPhase1Catalog.UpsertAsync(new RegisteredProject
        {
            ProjectId = "legacy-only",
            DisplayName = "Legacy Only",
            RepositoryRoot = GetRepositoryRoot()
        });

        var secondCatalog = new GrainBackedProjectCatalog(
            projectGrainFactory,
            projectRegistryGrainFactory,
            restartedPhase1Catalog);

        var persisted = await secondCatalog.ListAsync();

        Assert.Collection(imported, project => Assert.Equal("alpha", project.ProjectId));
        Assert.Collection(persisted, project => Assert.Equal("alpha", project.ProjectId));
    }

    [Fact]
    public async Task GrainBackedProjectCatalogPersistsMetadataUpdatesAndProjectIsolationAcrossReactivation()
    {
        var projectGrainFactory = new TestProjectGrainFactory();
        var projectRegistryGrainFactory = new TestProjectRegistryGrainFactory();
        var firstCatalog = new GrainBackedProjectCatalog(
            projectGrainFactory,
            projectRegistryGrainFactory,
            new InMemoryProjectCatalog());

        await firstCatalog.UpsertAsync(new RegisteredProject
        {
            ProjectId = "alpha",
            DisplayName = "Alpha",
            RepositoryRoot = GetRepositoryRoot()
        });
        await firstCatalog.UpsertAsync(new RegisteredProject
        {
            ProjectId = "beta",
            DisplayName = "Beta",
            RepositoryRoot = Path.Combine(GetRepositoryRoot(), "src")
        });
        await firstCatalog.UpsertAsync(new RegisteredProject
        {
            ProjectId = "alpha",
            DisplayName = "Alpha Updated",
            RepositoryRoot = Path.Combine(GetRepositoryRoot(), "tests")
        });

        var secondCatalog = new GrainBackedProjectCatalog(
            projectGrainFactory,
            projectRegistryGrainFactory,
            new InMemoryProjectCatalog());

        var alpha = await secondCatalog.GetAsync("alpha");
        var beta = await secondCatalog.GetAsync("beta");
        var persistedProjects = (await secondCatalog.ListAsync())
            .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotNull(alpha);
        Assert.Equal("Alpha Updated", alpha!.DisplayName);
        Assert.Equal(Path.Combine(GetRepositoryRoot(), "tests"), alpha.RepositoryRoot);

        Assert.NotNull(beta);
        Assert.Equal("Beta", beta!.DisplayName);
        Assert.Equal(Path.Combine(GetRepositoryRoot(), "src"), beta.RepositoryRoot);

        Assert.Collection(
            persistedProjects,
            project =>
            {
                Assert.Equal("alpha", project.ProjectId);
                Assert.Equal("Alpha Updated", project.DisplayName);
            },
            project =>
            {
                Assert.Equal("beta", project.ProjectId);
                Assert.Equal("Beta", project.DisplayName);
            });
    }

    [Fact]
    public async Task ProjectGrainRejectsMismatchedProjectIdentifier()
    {
        var projectGrain = new TestProjectGrainFactory().GetGrain("broker");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => projectGrain.UpsertAsync(new RegisteredProjectRecord
        {
            ProjectId = "mobile",
            DisplayName = "Mobile",
            RepositoryRoot = GetRepositoryRoot()
        }));

        Assert.Equal("project", exception.ParamName);
    }

    private static BrokerEnvelopeCommand<TPayload> CreateBrokerCommand<TPayload>(
        SessionMessageType messageType,
        string messageId,
        string correlationId,
        TPayload payload) =>
        new()
        {
            MessageType = messageType,
            MessageId = messageId,
            CorrelationId = correlationId,
            Payload = payload
        };

    private static MessageEnvelope<InputChunkPayload> CreateInputEnvelope(
        SessionDescriptor session,
        long clientSequence,
        long? acknowledgedSequence,
        string content,
        long generation = SessionEnvelopeContract.InitialGeneration) =>
        new()
        {
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            Generation = generation,
            MessageType = SessionMessageType.Input,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = clientSequence,
            AcknowledgedSequence = acknowledgedSequence,
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
        long generation,
        long fromSequenceInclusive,
        int maximumMessages = 10,
        ReplayRequestReason reason = ReplayRequestReason.GapDetected) =>
        new()
        {
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            Generation = generation,
            MessageType = SessionMessageType.ReplayRequest,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = clientSequence,
            MessageId = $"client-replay-{clientSequence}",
            CorrelationId = "corr-replay",
            Payload = new ReplayRequestPayload
            {
                FromSequenceInclusive = fromSequenceInclusive,
                MaximumMessages = maximumMessages,
                Reason = reason
            }
        };

    private static Task<MessageEnvelope<OutputChunkPayload>> RecordOutputAsync(
        GrainBackedSessionOrchestrator orchestrator,
        SessionDescriptor session,
        string messageId,
        string correlationId,
        string content) =>
        orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Output,
                messageId,
                correlationId,
                new OutputChunkPayload
                {
                    Content = content
                }));

    private static string GetRepositoryRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    private static int GetClientMessageGateCount(GrainBackedSessionOrchestrator orchestrator)
    {
        var field = typeof(GrainBackedSessionOrchestrator).GetField("_clientMessageGates", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to inspect client message gates.");
        var gates = field.GetValue(orchestrator) as System.Collections.IDictionary
            ?? throw new InvalidOperationException("Client message gate store had an unexpected shape.");
        return gates.Count;
    }

    private static RegisteredProjectRecord CloneProject(RegisteredProjectRecord source) =>
        new()
        {
            ProjectId = source.ProjectId,
            DisplayName = source.DisplayName,
            RepositoryRoot = source.RepositoryRoot
        };

    private sealed class TestSessionGrainFactory : ISessionGrainFactory
    {
        private readonly ConcurrentDictionary<string, TestPersistentState> _states = new(StringComparer.OrdinalIgnoreCase);
        private readonly int? _replayBufferCapacity;

        public TestSessionGrainFactory(int? replayBufferCapacity = null)
        {
            _replayBufferCapacity = replayBufferCapacity;
        }

        public ISessionGrain GetGrain(string sessionId)
        {
            var persistentState = _states.GetOrAdd(sessionId, static _ => new TestPersistentState());
            var grain = new SessionGrain(persistentState);

            if (_replayBufferCapacity.HasValue)
            {
                var field = typeof(SessionGrain).GetField("_replayBufferCapacity", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field is null)
                {
                    throw new InvalidOperationException("Unable to locate replay buffer capacity field.");
                }

                field.SetValue(grain, _replayBufferCapacity.Value);
            }

            return grain;
        }
    }

    private sealed class TestProjectGrainFactory : IProjectGrainFactory
    {
        private readonly ConcurrentDictionary<string, TestProjectPersistentState> _states = new(StringComparer.OrdinalIgnoreCase);

        public IProjectGrain GetGrain(string projectId)
        {
            var persistentState = _states.GetOrAdd(projectId, static _ => new TestProjectPersistentState());
            return new ProjectGrain(persistentState, () => projectId);
        }
    }

    private sealed class TestProjectRegistryGrainFactory : IProjectRegistryGrainFactory
    {
        private readonly TestProjectRegistryPersistentState _persistentState = new();

        public IProjectRegistryGrain GetGrain() => new ProjectRegistryGrain(_persistentState);
    }

    private sealed class TestPersistentState : IPersistentState<SessionGrainState>
    {
        private readonly object _syncRoot = new();
        private SessionGrainState _storedState = new();
        private bool _recordExists;
        private int _readStateCallCount;

        public int ReadStateCallCount => _readStateCallCount;

        public TimeSpan ReadDelay { get; init; }

        public string Etag { get; set; } = string.Empty;

        public bool RecordExists
        {
            get
            {
                lock (_syncRoot)
                {
                    return _recordExists;
                }
            }
        }

        public SessionGrainState State { get; set; } = new();

        public Task ClearStateAsync() => ClearStateAsync(CancellationToken.None);

        public Task ClearStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                _storedState = new SessionGrainState();
                State = new SessionGrainState();
                _recordExists = false;
                Etag = string.Empty;
            }

            return Task.CompletedTask;
        }

        public Task ReadStateAsync() => ReadStateAsync(CancellationToken.None);

        public async Task ReadStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readStateCallCount);
            if (ReadDelay > TimeSpan.Zero)
            {
                await Task.Delay(ReadDelay, cancellationToken);
            }

            lock (_syncRoot)
            {
                State = Clone(_storedState);
            }
        }

        public Task WriteStateAsync() => WriteStateAsync(CancellationToken.None);

        public Task WriteStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                _storedState = Clone(State);
                _recordExists = true;
                Etag = Guid.NewGuid().ToString("n");
            }

            return Task.CompletedTask;
        }

        private static SessionGrainState Clone(SessionGrainState source) =>
            new()
            {
                Descriptor = source.Descriptor is null
                    ? null
                    : new SessionDescriptorRecord
                    {
                        SessionId = source.Descriptor.SessionId,
                        ProjectId = source.Descriptor.ProjectId,
                        State = source.Descriptor.State,
                        CreatedAtUtc = source.Descriptor.CreatedAtUtc
                    },
                Generation = source.Generation,
                NextBrokerSequence = source.NextBrokerSequence,
                LastClientSequence = source.LastClientSequence,
                AcknowledgedSequence = source.AcknowledgedSequence,
                ReplayMessages = source.ReplayMessages.Select(Clone).ToList(),
                RecentEnvelopes = source.RecentEnvelopes.Select(Clone).ToList(),
                RecentEvents = source.RecentEvents.Select(Clone).ToList()
            };

        private static SessionEnvelopeRecord Clone(SessionEnvelopeRecord source) =>
            new()
            {
                ContractVersion = source.ContractVersion,
                ProjectId = source.ProjectId,
                SessionId = source.SessionId,
                Generation = source.Generation,
                MessageType = source.MessageType,
                Direction = source.Direction,
                Sequence = source.Sequence,
                ClientSequence = source.ClientSequence,
                AcknowledgedSequence = source.AcknowledgedSequence,
                TimestampUtc = source.TimestampUtc,
                MessageId = source.MessageId,
                CorrelationId = source.CorrelationId,
                CausationId = source.CausationId,
                PayloadJson = source.PayloadJson
            };

        private static SessionTelemetryEnvelopeRecord Clone(SessionTelemetryEnvelopeRecord source) =>
            new()
            {
                ObservedAtUtc = source.ObservedAtUtc,
                MessageType = source.MessageType,
                Direction = source.Direction,
                Generation = source.Generation,
                Sequence = source.Sequence,
                ClientSequence = source.ClientSequence,
                AcknowledgedSequence = source.AcknowledgedSequence,
                MessageId = source.MessageId,
                CorrelationId = source.CorrelationId,
                CausationId = source.CausationId,
                PayloadType = source.PayloadType,
                PayloadPreview = source.PayloadPreview
            };

        private static SessionTelemetryEventRecord Clone(SessionTelemetryEventRecord source) =>
            new()
            {
                ObservedAtUtc = source.ObservedAtUtc,
                EventType = source.EventType,
                Summary = source.Summary,
                MessageType = source.MessageType,
                Generation = source.Generation,
                Sequence = source.Sequence,
                ClientSequence = source.ClientSequence,
                ExpectedClientSequence = source.ExpectedClientSequence,
                LastAcceptedClientSequence = source.LastAcceptedClientSequence,
                AcknowledgedSequence = source.AcknowledgedSequence,
                MessageId = source.MessageId,
                CorrelationId = source.CorrelationId,
                CausationId = source.CausationId,
                ValidationStatus = source.ValidationStatus,
                GapDetected = source.GapDetected,
                RequestedFromSequence = source.RequestedFromSequence,
                RequestedToSequence = source.RequestedToSequence,
                AvailableFromSequence = source.AvailableFromSequence,
                AvailableToSequence = source.AvailableToSequence,
                HasMore = source.HasMore,
                IsComplete = source.IsComplete,
                Reason = source.Reason
            };
    }

    private sealed class TestProjectPersistentState : IPersistentState<ProjectGrainState>
    {
        private readonly object _syncRoot = new();
        private ProjectGrainState _storedState = new();
        private bool _recordExists;

        public string Etag { get; set; } = string.Empty;

        public bool RecordExists
        {
            get
            {
                lock (_syncRoot)
                {
                    return _recordExists;
                }
            }
        }

        public ProjectGrainState State { get; set; } = new();

        public Task ClearStateAsync() => ClearStateAsync(CancellationToken.None);

        public Task ClearStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                _storedState = new ProjectGrainState();
                State = new ProjectGrainState();
                _recordExists = false;
                Etag = string.Empty;
            }

            return Task.CompletedTask;
        }

        public Task ReadStateAsync() => ReadStateAsync(CancellationToken.None);

        public Task ReadStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                State = Clone(_storedState);
            }

            return Task.CompletedTask;
        }

        public Task WriteStateAsync() => WriteStateAsync(CancellationToken.None);

        public Task WriteStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                _storedState = Clone(State);
                _recordExists = true;
                Etag = Guid.NewGuid().ToString("n");
            }

            return Task.CompletedTask;
        }

        private static ProjectGrainState Clone(ProjectGrainState source) =>
            new()
            {
                Project = source.Project is null
                    ? null
                    : CloneProject(source.Project)
            };
    }

    private sealed class TestProjectRegistryPersistentState : IPersistentState<ProjectRegistryGrainState>
    {
        private readonly object _syncRoot = new();
        private ProjectRegistryGrainState _storedState = new();
        private bool _recordExists;

        public string Etag { get; set; } = string.Empty;

        public bool RecordExists
        {
            get
            {
                lock (_syncRoot)
                {
                    return _recordExists;
                }
            }
        }

        public ProjectRegistryGrainState State { get; set; } = new();

        public Task ClearStateAsync() => ClearStateAsync(CancellationToken.None);

        public Task ClearStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                _storedState = new ProjectRegistryGrainState();
                State = new ProjectRegistryGrainState();
                _recordExists = false;
                Etag = string.Empty;
            }

            return Task.CompletedTask;
        }

        public Task ReadStateAsync() => ReadStateAsync(CancellationToken.None);

        public Task ReadStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                State = Clone(_storedState);
            }

            return Task.CompletedTask;
        }

        public Task WriteStateAsync() => WriteStateAsync(CancellationToken.None);

        public Task WriteStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                _storedState = Clone(State);
                _recordExists = true;
                Etag = Guid.NewGuid().ToString("n");
            }

            return Task.CompletedTask;
        }

        private static ProjectRegistryGrainState Clone(ProjectRegistryGrainState source) =>
            new()
            {
                Projects = source.Projects.Select(CloneProject).ToList(),
                Phase1SeedImported = source.Phase1SeedImported,
                Phase1SeedImportedAtUtc = source.Phase1SeedImportedAtUtc
            };
    }
}
