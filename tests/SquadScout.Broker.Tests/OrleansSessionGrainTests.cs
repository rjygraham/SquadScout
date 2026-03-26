using System.Collections.Concurrent;
using Orleans.Core;
using Orleans.Runtime;
using SquadScout.Broker.Configuration;
using SquadScout.Broker.Orleans;
using SquadScout.Broker.Projects;
using SquadScout.Broker.Pty;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Broker.Tests.TestDoubles;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class OrleansSessionGrainTests
{
    [Fact]
    public void SessionGrainStatusSnapshotReportsDurableRuntimeMode()
    {
        var snapshot = OrleansHostStatusSnapshot.SessionGrains(
            new OrleansHostOptions
            {
                Enabled = true,
                ClusterId = "test-cluster",
                ServiceId = "test-service",
                SiloPort = 11111,
                GatewayPort = 30000,
                StorageProvider = OrleansHostOptions.DefaultStorageProvider,
                AdoNetInvariant = OrleansHostOptions.DefaultAdoNetInvariant
            },
            new OrleansSchemaBootstrapResult
            {
                ConnectionString = "Data Source=.squadscout\\orleans\\tests.db;Mode=ReadWriteCreate;Cache=Shared",
                Invariant = OrleansHostOptions.DefaultAdoNetInvariant,
                DatabasePath = "D:\\GitHub\\SquadScout-19\\.squadscout\\orleans\\tests.db",
                SchemaReady = true,
                SchemaCreatedThisRun = true
            },
            new OrleansSqliteCompatibilityResult
            {
                ConfiguredInvariant = OrleansHostOptions.DefaultAdoNetInvariant,
                Applied = true,
                Note = "SQLite compatibility shim applied."
            });

        Assert.True(snapshot.Enabled);
        Assert.Equal("session-grains", snapshot.HostMode);
        Assert.Equal("durable-grain", snapshot.SessionStateMode);
        Assert.Contains("durable session replay state", snapshot.Summary, StringComparison.OrdinalIgnoreCase);
    }

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
            message => Assert.Equal(1, message.Sequence),
            message => Assert.Equal(2, message.Sequence));
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
    public async Task RelayPipelineRemainsCompatibleWithGrainBackedSessionState()
    {
        var projectCatalog = new InMemoryProjectCatalog();
        await projectCatalog.UpsertAsync(new RegisteredProject
        {
            ProjectId = "broker",
            DisplayName = "Broker",
            RepositoryRoot = GetRepositoryRoot()
        });

        var relayPublisher = new RecordingRelayPublisher();
        var orchestrator = new GrainBackedSessionOrchestrator(relayPublisher, new TestSessionGrainFactory());
        var ptyHost = new MockPtyHost();
        await using var relay = new InMemorySessionRelay(
            projectCatalog,
            orchestrator,
            ptyHost,
            new PtySessionEnvelopePump(orchestrator),
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
        string content) =>
        new()
        {
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            Generation = SessionEnvelopeContract.InitialGeneration,
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
                MaximumMessages = 10,
                Reason = reason
            }
        };

    private static string GetRepositoryRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    private sealed class TestSessionGrainFactory : ISessionGrainFactory
    {
        private readonly ConcurrentDictionary<string, TestPersistentState> _states = new(StringComparer.OrdinalIgnoreCase);

        public ISessionGrain GetGrain(string sessionId)
        {
            var persistentState = _states.GetOrAdd(sessionId, static _ => new TestPersistentState());
            return new SessionGrain(persistentState);
        }
    }

    private sealed class TestPersistentState : IPersistentState<SessionGrainState>
    {
        private readonly object _syncRoot = new();
        private SessionGrainState _storedState = new();
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
}
