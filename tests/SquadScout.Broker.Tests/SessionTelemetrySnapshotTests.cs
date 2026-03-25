using System.Text.Json;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Security;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class SessionTelemetrySnapshotTests
{
    [Fact]
    public async Task ExportTelemetryCapturesReplayGapAndGenerationResetContext()
    {
        var orchestrator = new InMemorySessionOrchestrator(new NullRelayPublisher(), new SessionSequenceValidator(), replayBufferCapacity: 2);
        var session = await StartSessionAsync(orchestrator);

        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Output,
                "broker-output-1",
                "corr-broker",
                new OutputChunkPayload { Content = "one" }));
        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Output,
                "broker-output-2",
                "corr-broker",
                new OutputChunkPayload { Content = "two" }));
        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Output,
                "broker-output-3",
                "corr-broker",
                new OutputChunkPayload { Content = "three" }));

        var accepted = await orchestrator.AcceptClientMessageAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 1, "status\n"),
            static (_, _) => Task.CompletedTask);

        var replay = await orchestrator.ReplayAsync(
            session.SessionId,
            CreateReplayRequest(session, clientSequence: 2, fromSequenceInclusive: 1, ReplayRequestReason.GapDetected));

        var currentGeneration = await orchestrator.ResetGenerationAsync(session.SessionId);
        var telemetry = await orchestrator.ExportTelemetryAsync(session.SessionId);

        Assert.Equal(SequenceValidationStatus.Accepted, accepted.Status);
        Assert.True(replay.Payload.GapDetected);
        Assert.Equal(currentGeneration, telemetry.Sequencing.Generation);
        Assert.Equal(0, telemetry.Sequencing.LastBrokerSequence);
        Assert.Equal(0, telemetry.ReplayBuffer.Count);

        var replayEvent = Assert.Single(
            telemetry.RecentEvents,
            evt => evt.EventType == SessionTelemetryEventType.ReplayResponseCreated);
        Assert.True(replayEvent.GapDetected);
        Assert.Equal(1, replayEvent.RequestedFromSequence);
        Assert.Equal(2, replayEvent.AvailableFromSequence);
        Assert.Equal(3, replayEvent.AvailableToSequence);
        Assert.Equal("corr-replay", replayEvent.CorrelationId);

        var resetEvent = Assert.Single(
            telemetry.RecentEvents,
            evt => evt.EventType == SessionTelemetryEventType.GenerationReset);
        Assert.Equal(currentGeneration, resetEvent.Generation);
        Assert.Contains("advanced from 1 to 2", resetEvent.Reason, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            telemetry.RecentEnvelopes,
            envelope => envelope.MessageType == SessionMessageType.Input
                        && envelope.ClientSequence == 1
                        && envelope.CorrelationId == "corr-input");
    }

    [Fact]
    public async Task ExportTelemetryRedactsSensitivePayloadPreview()
    {
        var orchestrator = new InMemorySessionOrchestrator(new NullRelayPublisher(), new SessionSequenceValidator());
        var session = await StartSessionAsync(orchestrator);

        await orchestrator.AcceptClientMessageAsync(
            session.SessionId,
            CreateInputEnvelope(session, clientSequence: 1, "password=swordfish&token=def"),
            static (_, _) => Task.CompletedTask);

        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            CreateBrokerCommand(
                SessionMessageType.Output,
                "broker-output-secret",
                "corr-secret",
                new OutputChunkPayload
                {
                    Content = "{\"password\":\"swordfish\",\"Authorization\":\"Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.signature\",\"token\":\"abc\"}"
                }));

        var telemetry = await orchestrator.ExportTelemetryAsync(session.SessionId);
        var exportJson = JsonSerializer.Serialize(telemetry, SessionMessageSerializer.DefaultOptions);

        Assert.DoesNotContain("swordfish", exportJson, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.signature", exportJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"abc\"", exportJson, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.RedactedValue, exportJson, StringComparison.Ordinal);
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
        long fromSequenceInclusive,
        ReplayRequestReason reason) =>
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
                Reason = reason
            }
        };
}
