using Microsoft.Extensions.Logging;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class InMemorySessionOrchestratorReplayTests
{
    [Fact]
    public async Task ReplayDetectsOverflowAndSkipsHeartbeatControlFrames()
    {
        var orchestrator = new InMemorySessionOrchestrator(new NullRelayPublisher(), new SessionSequenceValidator(), replayBufferCapacity: 3);
        var session = await StartSessionAsync(orchestrator);

        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(SessionMessageType.Output, "broker-output-1", "corr-output", new { text = "one" }));
        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(SessionMessageType.Heartbeat, "broker-heartbeat-1", "corr-heartbeat", new HeartbeatPayload()));
        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(SessionMessageType.Output, "broker-output-2", "corr-output", new { text = "two" }));
        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(SessionMessageType.SessionLifecycle, "broker-lifecycle-3", "corr-lifecycle", new { state = "running" }));
        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(SessionMessageType.Output, "broker-output-4", "corr-output", new { text = "four" }));

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
    public async Task ReplayReturnsResetBoundaryWhenGenerationChanges()
    {
        var orchestrator = new InMemorySessionOrchestrator(new NullRelayPublisher(), new SessionSequenceValidator());
        var session = await StartSessionAsync(orchestrator);

        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(SessionMessageType.Output, "broker-output-1", "corr-reset", new { text = "before reset" }));

        var generation = await orchestrator.ResetGenerationAsync(session.SessionId);

        var postResetEnvelope = await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(SessionMessageType.Output, "broker-output-2", "corr-reset", new { text = "after reset" }));

        var replay = await orchestrator.ReplayAsync(
            session.SessionId,
            new MessageEnvelope<ReplayRequestPayload>
            {
                ProjectId = session.ProjectId,
                SessionId = session.SessionId,
                Generation = generation - 1,
                MessageType = SessionMessageType.ReplayRequest,
                Direction = MessageDirection.ClientToBroker,
                ClientSequence = 1,
                MessageId = "client-replay-reset",
                CorrelationId = "corr-reset",
                Payload = new ReplayRequestPayload
                {
                    FromSequenceInclusive = 2,
                    Reason = ReplayRequestReason.ReconnectResume
                }
            });

        Assert.Equal(1, postResetEnvelope.Sequence);
        Assert.Equal(generation, replay.Generation);
        Assert.Equal(generation, replay.Payload.Generation);
        Assert.True(replay.Payload.GapDetected);
        Assert.Equal(1, replay.Payload.AvailableFromSequence);
        Assert.Equal(1, replay.Payload.AvailableToSequence);
        Assert.Empty(replay.Payload.Messages);
    }

    [Fact]
    public async Task ReplayRejectsCrossSessionEnvelopeTargets()
    {
        var orchestrator = new InMemorySessionOrchestrator(new NullRelayPublisher(), new SessionSequenceValidator());
        var session = await StartSessionAsync(orchestrator);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.ReplayAsync(
            session.SessionId,
            new MessageEnvelope<ReplayRequestPayload>
            {
                ProjectId = session.ProjectId,
                SessionId = "other-session",
                Generation = SessionEnvelopeContract.InitialGeneration,
                MessageType = SessionMessageType.ReplayRequest,
                Direction = MessageDirection.ClientToBroker,
                ClientSequence = 1,
                MessageId = "client-replay-mismatch",
                CorrelationId = "corr-mismatch",
                Payload = new ReplayRequestPayload
                {
                    FromSequenceInclusive = 1
                }
            }));

        Assert.Contains("session id", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplayRejectsCrossProjectEnvelopeTargets()
    {
        var orchestrator = new InMemorySessionOrchestrator(new NullRelayPublisher(), new SessionSequenceValidator());
        var session = await StartSessionAsync(orchestrator);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.ReplayAsync(
            session.SessionId,
            new MessageEnvelope<ReplayRequestPayload>
            {
                ProjectId = "other-project",
                SessionId = session.SessionId,
                Generation = SessionEnvelopeContract.InitialGeneration,
                MessageType = SessionMessageType.ReplayRequest,
                Direction = MessageDirection.ClientToBroker,
                ClientSequence = 1,
                MessageId = "client-replay-project-mismatch",
                CorrelationId = "corr-project-mismatch",
                Payload = new ReplayRequestPayload
                {
                    FromSequenceInclusive = 1
                }
            }));

        Assert.Contains("project id", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplayReturnsResetBoundaryWhenFutureGenerationRequested()
    {
        var orchestrator = new InMemorySessionOrchestrator(new NullRelayPublisher(), new SessionSequenceValidator());
        var session = await StartSessionAsync(orchestrator);

        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(SessionMessageType.Output, "broker-output-1", "corr-future", new { text = "current-gen" }));

        // Request replay for a generation that does not yet exist.
        var replay = await orchestrator.ReplayAsync(
            session.SessionId,
            new MessageEnvelope<ReplayRequestPayload>
            {
                ProjectId = session.ProjectId,
                SessionId = session.SessionId,
                Generation = SessionEnvelopeContract.InitialGeneration + 99,
                MessageType = SessionMessageType.ReplayRequest,
                Direction = MessageDirection.ClientToBroker,
                ClientSequence = 1,
                MessageId = "client-replay-future",
                CorrelationId = "corr-future",
                Payload = new ReplayRequestPayload
                {
                    FromSequenceInclusive = 1,
                    Reason = ReplayRequestReason.ReconnectResume
                }
            });

        Assert.Equal(SessionEnvelopeContract.InitialGeneration, replay.Generation);
        Assert.Equal(SessionEnvelopeContract.InitialGeneration, replay.Payload.Generation);
        Assert.True(replay.Payload.GapDetected);
        Assert.Equal(1, replay.Payload.AvailableFromSequence);
        Assert.Equal(1, replay.Payload.AvailableToSequence);
        Assert.Empty(replay.Payload.Messages);
    }

    [Fact]
    public async Task GapDetectedInputLogsCorrelationRichPayloadSafeWarning()
    {
        var logger = new RecordingLogger<InMemorySessionOrchestrator>();
        var orchestrator = new InMemorySessionOrchestrator(
            new NullRelayPublisher(),
            new SessionSequenceValidator(),
            logger: logger);
        var session = await StartSessionAsync(orchestrator);

        _ = await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(SessionMessageType.Output, "broker-output-1", "corr-broker", new { text = "ready" }));

        _ = await orchestrator.AcceptClientMessageAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 1, acknowledgedSequence: 1, content: "first\n"),
            static (_, _) => Task.CompletedTask);

        var gap = await orchestrator.AcceptClientMessageAsync(
            session.SessionId,
            CreateInputEnvelope(
                session,
                clientSequence: 3,
                acknowledgedSequence: 1,
                content: "password=swordfish\n",
                messageId: "client-gap-3",
                correlationId: "corr-gap-3"),
            static (_, _) => Task.CompletedTask);

        Assert.Equal(SequenceValidationStatus.GapDetected, gap.Status);

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(session.ProjectId, warning.Message, StringComparison.Ordinal);
        Assert.Contains(session.SessionId, warning.Message, StringComparison.Ordinal);
        Assert.Contains("generation 1", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MessageId=client-gap-3", warning.Message, StringComparison.Ordinal);
        Assert.Contains("CorrelationId=corr-gap-3", warning.Message, StringComparison.Ordinal);
        Assert.Contains("expected 2", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("received 3", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("swordfish", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("password=", warning.Message, StringComparison.Ordinal);
    }

    private static async Task<SessionDescriptor> StartSessionAsync(InMemorySessionOrchestrator orchestrator) =>
        await orchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

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
        long acknowledgedSequence,
        string content,
        string? messageId = null,
        string? correlationId = null) =>
        new()
        {
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.Input,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = clientSequence,
            AcknowledgedSequence = acknowledgedSequence,
            MessageId = messageId ?? $"client-input-{clientSequence}",
            CorrelationId = correlationId ?? $"corr-input-{clientSequence}",
            Payload = new InputChunkPayload
            {
                Content = content
            }
        };

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
