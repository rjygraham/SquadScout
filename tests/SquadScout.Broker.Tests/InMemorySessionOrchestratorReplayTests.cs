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
        Assert.True(replay.Payload.GapDetected);
        Assert.Equal(2, replay.Payload.AvailableFromSequence);
        Assert.Equal(4, replay.Payload.AvailableToSequence);
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
}
