using System.Text.Json;
using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Tests;

public sealed class MessageEnvelopeContractTests
{
    [Fact]
    public void EnvelopeSerializationUsesStableVersionedCamelCaseContract()
    {
        var envelope = new MessageEnvelope<HeartbeatPayload>
        {
            ProjectId = "broker",
            SessionId = "session-123",
            MessageType = SessionMessageType.Heartbeat,
            Direction = MessageDirection.BrokerToClient,
            Sequence = 42,
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

        Assert.Contains("\"contractVersion\":1", json);
        Assert.Contains("\"messageType\":\"heartbeat\"", json);
        Assert.Contains("\"direction\":\"brokerToClient\"", json);
        Assert.Contains("\"acknowledgedSequence\":41", json);
        Assert.Contains("\"expectedIntervalSeconds\":30", json);
        Assert.Contains("\"senderInstanceId\":\"broker-local\"", json);
    }

    [Fact]
    public void ReplayResponsesCanCarryOrderedEnvelopeSnapshots()
    {
        var replayedOutput = new MessageEnvelope<JsonElement>
        {
            ProjectId = "broker",
            SessionId = "session-123",
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

        Assert.Collection(
            envelope.Payload.Messages,
            message =>
            {
                Assert.Equal(98, message.Sequence);
                Assert.Equal(SessionMessageType.Output, message.MessageType);
                Assert.Equal("resume output", message.Payload.GetProperty("text").GetString());
            });
        Assert.Equal(1, envelope.Payload.AvailableFromSequence);
        Assert.Equal(99, envelope.Payload.AvailableToSequence);
    }

    [Fact]
    public void ReplayRequestsAndContractVersionDocumentCompatibilityExpectations()
    {
        var request = new MessageEnvelope<ReplayRequestPayload>
        {
            ProjectId = "broker",
            SessionId = "session-123",
            MessageType = SessionMessageType.ReplayRequest,
            Direction = MessageDirection.ClientToBroker,
            Sequence = 120,
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

        Assert.Equal(SessionEnvelopeContract.CurrentVersion, request.ContractVersion);
        Assert.Equal(118, request.Payload.FromSequenceInclusive);
        Assert.Equal(119, request.Payload.ToSequenceInclusive);
        Assert.Equal(25, request.Payload.MaximumMessages);
        Assert.Equal(ReplayRequestReason.GapDetected, request.Payload.Reason);
    }

    [Fact]
    public void MinimalEnvelopesCanDeserializeWithoutFutureOptionalFields()
    {
        const string json = """
            {
              "contractVersion": 1,
              "projectId": "broker",
              "sessionId": "session-123",
              "messageType": "heartbeat",
              "direction": "brokerToClient",
              "sequence": 5,
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
        Assert.Null(envelope.AcknowledgedSequence);
        Assert.Null(envelope.CausationId);
        Assert.Equal(30, envelope.Payload.ExpectedIntervalSeconds);
        Assert.True(envelope.Payload.ReplayRequested);
    }
}
