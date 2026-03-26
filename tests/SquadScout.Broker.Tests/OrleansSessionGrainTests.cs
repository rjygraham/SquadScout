using System.Collections.Concurrent;
using System.Reflection;
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
using SquadScout.Contracts.Realtime;
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
        Assert.Equal("in-memory", snapshot.ProjectStateMode);
        Assert.Contains("durable session replay state", snapshot.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionProjectGrainStatusSnapshotReportsDurableProjectMode()
    {
        var snapshot = OrleansHostStatusSnapshot.SessionProjectGrains(
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
                DatabasePath = "D:\\GitHub\\SquadScout-20\\.squadscout\\orleans\\tests.db",
                SchemaReady = true,
                SchemaCreatedThisRun = false
            },
            new OrleansSqliteCompatibilityResult
            {
                ConfiguredInvariant = OrleansHostOptions.DefaultAdoNetInvariant,
                Applied = true,
                Note = "SQLite compatibility shim applied."
            });

        Assert.True(snapshot.Enabled);
        Assert.Equal("session-project-grains", snapshot.HostMode);
        Assert.Equal("durable-grain", snapshot.SessionStateMode);
        Assert.Equal("durable-grain", snapshot.ProjectStateMode);
        Assert.Contains("project registration catalog", snapshot.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restart active sessions", snapshot.Note, StringComparison.OrdinalIgnoreCase);
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

        public ISessionGrain GetGrain(string sessionId)
        {
            var persistentState = _states.GetOrAdd(sessionId, static _ => new TestPersistentState());
            return new SessionGrain(persistentState);
        }
    }

    private sealed class TestProjectGrainFactory : IProjectGrainFactory
    {
        private readonly ConcurrentDictionary<string, TestProjectPersistentState> _states = new(StringComparer.OrdinalIgnoreCase);

        public IProjectGrain GetGrain(string projectId)
        {
            var persistentState = _states.GetOrAdd(projectId, static _ => new TestProjectPersistentState());
            return new ProjectGrain(persistentState);
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
