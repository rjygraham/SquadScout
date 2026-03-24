using System.Text.Json;
using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Tests;

public sealed class MessageEnvelopeContractTests
{
    [Fact]
    public void HeartbeatControlFramesSerializeGenerationAndAckWithoutReplaySequence()
    {
        var envelope = new MessageEnvelope<HeartbeatPayload>
        {
            ProjectId = "broker",
            SessionId = "session-123",
            Generation = 7,
            MessageType = SessionMessageType.Heartbeat,
            Direction = MessageDirection.BrokerToClient,
            AcknowledgedSequence = 41,
            TimestampUtc = DateTimeOffset.Parse("2026-03-24T18:00:00+00:00"),
            MessageId = "msg-42",
            CorrelationId = "corr-42",
            CausationId = "msg-41",
            Payload = new HeartbeatPayload
            {
                ReplayRequested = false,
                ExpectedIntervalSeconds = 30,
                SenderInstanceId = "broker-local"
            }
        };

        var json = JsonSerializer.Serialize(envelope, SessionMessageSerializer.DefaultOptions);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(SessionEnvelopeContract.CurrentVersion, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal(7, root.GetProperty("generation").GetInt64());
        Assert.Equal("heartbeat", root.GetProperty("messageType").GetString());
        Assert.Equal("brokerToClient", root.GetProperty("direction").GetString());
        Assert.Equal(41, root.GetProperty("acknowledgedSequence").GetInt64());
        Assert.False(root.TryGetProperty("sequence", out _));
        Assert.False(root.TryGetProperty("clientSequence", out _));
        Assert.Equal(30, root.GetProperty("payload").GetProperty("expectedIntervalSeconds").GetInt32());
        Assert.Equal("broker-local", root.GetProperty("payload").GetProperty("senderInstanceId").GetString());
    }

    [Fact]
    public void ReplayResponsesCanCarryOrderedEnvelopeSnapshotsWithinGenerationBoundary()
    {
        var replayedOutput = new MessageEnvelope<JsonElement>
        {
            ProjectId = "broker",
            SessionId = "session-123",
            Generation = 3,
            MessageType = SessionMessageType.Output,
            Direction = MessageDirection.BrokerToClient,
            Sequence = 98,
            AcknowledgedSequence = 97,
            TimestampUtc = DateTimeOffset.Parse("2026-03-24T18:01:00+00:00"),
            MessageId = "msg-98",
            CorrelationId = "corr-replay",
            Payload = JsonSerializer.SerializeToElement(new
            {
                stream = "stdout",
                text = "resume output"
            }, SessionMessageSerializer.DefaultOptions)
        };

        var replay = new ReplayResponsePayload
        {
            Generation = 3,
            FromSequenceInclusive = 98,
            ToSequenceInclusive = 98,
            AvailableFromSequence = 1,
            AvailableToSequence = 99,
            IsComplete = true,
            HasMore = false,
            GapDetected = false,
            Messages = [replayedOutput]
        };

        var envelope = new MessageEnvelope<ReplayResponsePayload>
        {
            ProjectId = "broker",
            SessionId = "session-123",
            Generation = 3,
            MessageType = SessionMessageType.ReplayResponse,
            Direction = MessageDirection.BrokerToClient,
            Sequence = 99,
            AcknowledgedSequence = 97,
            TimestampUtc = DateTimeOffset.Parse("2026-03-24T18:01:01+00:00"),
            MessageId = "msg-99",
            CorrelationId = "corr-replay",
            CausationId = "gap-detected-97",
            Payload = replay
        };
        var json = JsonSerializer.Serialize(envelope, SessionMessageSerializer.DefaultOptions);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var payload = root.GetProperty("payload");
        var replayedMessage = payload.GetProperty("messages")[0];

        Assert.Collection(
            envelope.Payload.Messages,
            message =>
            {
                Assert.Equal(3, message.Generation);
                Assert.Equal(98, message.Sequence);
                Assert.Equal(SessionMessageType.Output, message.MessageType);
                Assert.Equal("resume output", message.Payload.GetProperty("text").GetString());
            });
        Assert.Equal(3, envelope.Generation);
        Assert.Equal(3, envelope.Payload.Generation);
        Assert.Equal(1, envelope.Payload.AvailableFromSequence);
        Assert.Equal(99, envelope.Payload.AvailableToSequence);
        Assert.Equal(3, root.GetProperty("generation").GetInt64());
        Assert.Equal(3, payload.GetProperty("generation").GetInt64());
        Assert.Equal(3, replayedMessage.GetProperty("generation").GetInt64());
        Assert.Equal(98, replayedMessage.GetProperty("sequence").GetInt64());
    }

    [Fact]
    public void ClientReplayRequestsUseClientSequenceInsteadOfBrokerSequence()
    {
        var request = new MessageEnvelope<ReplayRequestPayload>
        {
            ProjectId = "broker",
            SessionId = "session-123",
            Generation = 3,
            MessageType = SessionMessageType.ReplayRequest,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = 120,
            AcknowledgedSequence = 117,
            TimestampUtc = DateTimeOffset.Parse("2026-03-24T18:02:00+00:00"),
            MessageId = "msg-120",
            CorrelationId = "corr-gap-118",
            Payload = new ReplayRequestPayload
            {
                FromSequenceInclusive = 118,
                ToSequenceInclusive = 119,
                MaximumMessages = 25,
                Reason = ReplayRequestReason.GapDetected
            }
        };
        var json = JsonSerializer.Serialize(request, SessionMessageSerializer.DefaultOptions);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(SessionEnvelopeContract.CurrentVersion, request.ContractVersion);
        Assert.Equal(3, request.Generation);
        Assert.Null(request.Sequence);
        Assert.Equal(120, request.ClientSequence);
        Assert.Equal(118, request.Payload.FromSequenceInclusive);
        Assert.Equal(119, request.Payload.ToSequenceInclusive);
        Assert.Equal(25, request.Payload.MaximumMessages);
        Assert.Equal(ReplayRequestReason.GapDetected, request.Payload.Reason);
        Assert.Equal(3, root.GetProperty("generation").GetInt64());
        Assert.Equal(120, root.GetProperty("clientSequence").GetInt64());
        Assert.False(root.TryGetProperty("sequence", out _));
    }

    [Fact]
    public void GenerationMismatchDefinesOrderedStateResetBoundaryForReconnect()
    {
        var request = new MessageEnvelope<ReplayRequestPayload>
        {
            ProjectId = "broker",
            SessionId = "session-123",
            Generation = 3,
            MessageType = SessionMessageType.ReplayRequest,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = 200,
            AcknowledgedSequence = 120,
            TimestampUtc = DateTimeOffset.Parse("2026-03-24T18:02:30+00:00"),
            MessageId = "client-msg-200",
            CorrelationId = "corr-resume",
            Payload = new ReplayRequestPayload
            {
                FromSequenceInclusive = 121,
                Reason = ReplayRequestReason.ReconnectResume
            }
        };

        var response = new MessageEnvelope<ReplayResponsePayload>
        {
            ProjectId = "broker",
            SessionId = "session-123",
            Generation = 4,
            MessageType = SessionMessageType.ReplayResponse,
            Direction = MessageDirection.BrokerToClient,
            Sequence = 1,
            TimestampUtc = DateTimeOffset.Parse("2026-03-24T18:02:31+00:00"),
            MessageId = "msg-reset-1",
            CorrelationId = "corr-resume",
            CausationId = request.MessageId,
            Payload = new ReplayResponsePayload
            {
                Generation = 4,
                FromSequenceInclusive = 1,
                ToSequenceInclusive = 1,
                AvailableFromSequence = 1,
                AvailableToSequence = 1,
                GapDetected = true,
                Messages = []
            }
        };

        Assert.NotEqual(request.Generation, response.Generation);
        Assert.Equal(response.Generation, response.Payload.Generation);
        Assert.Equal(1, response.Sequence);
        Assert.Null(response.AcknowledgedSequence);
        Assert.True(response.Payload.GapDetected);
        Assert.Equal(1, response.Payload.AvailableFromSequence);
        Assert.Equal(1, response.Payload.AvailableToSequence);
    }

    [Fact]
    public void MinimalEnvelopesCanDeserializeWithoutFutureOptionalFields()
    {
        const string json = """
            {
              "contractVersion": 1,
              "projectId": "broker",
              "sessionId": "session-123",
              "generation": 1,
              "messageType": "heartbeat",
              "direction": "brokerToClient",
              "timestampUtc": "2026-03-24T18:03:00+00:00",
              "messageId": "msg-5",
              "correlationId": "corr-5",
              "payload": {
                "replayRequested": true,
                "expectedIntervalSeconds": 30,
                "senderInstanceId": "broker-local"
              }
            }
            """;

        var envelope = JsonSerializer.Deserialize<MessageEnvelope<HeartbeatPayload>>(json, SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(envelope);
        Assert.Equal(SessionEnvelopeContract.CurrentVersion, envelope.ContractVersion);
        Assert.Equal(SessionEnvelopeContract.InitialGeneration, envelope.Generation);
        Assert.Null(envelope.Sequence);
        Assert.Null(envelope.ClientSequence);
        Assert.Null(envelope.AcknowledgedSequence);
        Assert.Null(envelope.CausationId);
        Assert.Equal(30, envelope.Payload.ExpectedIntervalSeconds);
        Assert.True(envelope.Payload.ReplayRequested);
    }
}
