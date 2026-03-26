using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using SquadScout.App.Configuration;
using SquadScout.App.Services;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Realtime;
using SquadScout.Contracts.Security;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.Tests;

public sealed class PubSubConnectionServiceTests
{
    [Fact]
    public async Task PrepareForSessionAsyncNegotiatesAndConnectsToSessionGroup()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-001")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), socket);

        var status = await service.PrepareForSessionAsync(session);

        Assert.Equal(MessageConnectionState.Connected, status.State);
        Assert.Equal("conn-001", status.ConnectionId);
        Assert.Equal("session:proj-01:session-abc", status.SessionGroup);
        Assert.Single(socket.SentTexts);

        using var joinCommand = JsonDocument.Parse(socket.SentTexts[0]);
        Assert.Equal("joinGroup", joinCommand.RootElement.GetProperty("type").GetString());
        Assert.Equal("session:proj-01:session-abc", joinCommand.RootElement.GetProperty("group").GetString());
    }

    [Fact]
    public async Task SendInputAsyncPublishesClientEnvelopeWithLatestAcknowledgedSequence()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-002")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), socket);
        await service.PrepareForSessionAsync(session);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        await WaitForAsync(() => service.RecentTraffic.Count == 1);

        await service.SendInputAsync("status");
        await WaitForAsync(() => service.RecentTraffic.Count == 2);

        using var sendCommand = JsonDocument.Parse(socket.SentTexts[1]);
        Assert.Equal("event", sendCommand.RootElement.GetProperty("type").GetString());
        Assert.Equal(SessionUpstreamEventNames.Input, sendCommand.RootElement.GetProperty("event").GetString());
        var envelope = sendCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<JsonElement>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(envelope);
        Assert.Equal(SessionMessageType.Input, envelope!.MessageType);
        Assert.Equal(MessageDirection.ClientToBroker, envelope.Direction);
        Assert.Equal(1, envelope.ClientSequence);
        Assert.Equal(1, envelope.AcknowledgedSequence);
        Assert.Equal("status", envelope.Payload.GetProperty("content").GetString());
    }

    [Fact]
    public async Task DuplicateLiveBrokerEnvelopeDoesNotAppendTwiceOrRegressAcknowledgement()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-dup-live")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), socket);
        await service.PrepareForSessionAsync(session);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 2)));
        await WaitForAsync(() =>
            service.RecentTraffic.Count == 2 &&
            service.RecentTraffic.Count(traffic =>
                traffic.Direction == MessageTrafficDirection.Incoming &&
                traffic.Envelope.Generation == SessionEnvelopeContract.InitialGeneration &&
                traffic.Envelope.Sequence == 1) == 1 &&
            service.RecentTraffic.Any(traffic =>
                traffic.Direction == MessageTrafficDirection.Incoming &&
                traffic.Envelope.Sequence == 2));

        await service.SendInputAsync("after-duplicate");
        await WaitForAsync(() => service.RecentTraffic.Count == 3);

        using var sendCommand = JsonDocument.Parse(socket.SentTexts[1]);
        var envelope = sendCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<JsonElement>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(envelope);
        Assert.Equal(2, envelope!.AcknowledgedSequence);
        Assert.Equal(1, envelope.ClientSequence);
        Assert.Equal(1, service.RecentTraffic.Count(traffic =>
            traffic.Direction == MessageTrafficDirection.Incoming &&
            traffic.Envelope.Generation == SessionEnvelopeContract.InitialGeneration &&
            traffic.Envelope.Sequence == 1));
    }

    [Fact]
    public async Task ReceiveLoopTracksGenerationGapAndUsesRecoveredBrokerAckForLaterInput()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-003")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), socket);
        await service.PrepareForSessionAsync(session);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 3)));
        await WaitForAsync(() =>
            socket.SentTexts.Count >= 2 &&
            service.CurrentStatus.Summary.Contains("gap", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(MessageTrafficDirection.Incoming, service.RecentTraffic[0].Direction);
        Assert.Equal(1, service.RecentTraffic[0].Envelope.Sequence);
        Assert.Equal(3, service.RecentTraffic[1].Envelope.Sequence);
        Assert.Contains("sequence gap", service.CurrentStatus.Summary, StringComparison.OrdinalIgnoreCase);

        using (var replayCommand = JsonDocument.Parse(socket.SentTexts[1]))
        {
            Assert.Equal(SessionUpstreamEventNames.ReplayRequest, replayCommand.RootElement.GetProperty("event").GetString());
            var replayRequest = replayCommand.RootElement.GetProperty("data")
                .Deserialize<MessageEnvelope<ReplayRequestPayload>>(SessionMessageSerializer.DefaultOptions);

            Assert.NotNull(replayRequest);
            Assert.Equal(SessionMessageType.ReplayRequest, replayRequest!.MessageType);
            Assert.Equal(ReplayRequestReason.GapDetected, replayRequest.Payload.Reason);
            Assert.Equal(SessionEnvelopeContract.InitialGeneration, replayRequest.Generation);
            Assert.Equal(2, replayRequest.Payload.FromSequenceInclusive);
            Assert.Equal(1, replayRequest.AcknowledgedSequence);
        }

        socket.EnqueueIncoming(GroupMessage(CreateReplayResponseEnvelope(
            session,
            messages:
            [
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 2)),
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 3))
            ])));
        await WaitForAsync(() => service.RecentTraffic.Count == 5);

        Assert.Equal(MessageConnectionState.Connected, service.CurrentStatus.State);
        Assert.DoesNotContain("gap", service.CurrentStatus.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, service.RecentTraffic.Count(traffic =>
            traffic.Direction == MessageTrafficDirection.Incoming &&
            traffic.Envelope.Sequence == 3));

        await service.SendInputAsync("after-recovery");
        await WaitForAsync(() => service.RecentTraffic.Count == 6);

        using var secondSendCommand = JsonDocument.Parse(socket.SentTexts[2]);
        var secondEnvelope = secondSendCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<JsonElement>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(secondEnvelope);
        Assert.Equal(SessionEnvelopeContract.InitialGeneration, secondEnvelope!.Generation);
        Assert.Equal(3, secondEnvelope.AcknowledgedSequence);
        Assert.Equal(1, secondEnvelope.ClientSequence);
    }

    [Fact]
    public async Task ReconnectAsyncReNegotiatesAfterUnexpectedDisconnect()
    {
        var session = CreateSession();
        var firstSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-first")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var secondSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-second")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var negotiationClient = new RecordingNegotiationClient(CreateNegotiationResponse(session));
        var service = CreateService(negotiationClient, firstSocket, secondSocket);
        await service.PrepareForSessionAsync(session);

        firstSocket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        await WaitForAsync(() => service.RecentTraffic.Count >= 1);

        firstSocket.EnqueueIncoming(SystemMessage("disconnected"));
        await WaitForAsync(() => service.CurrentStatus.State == MessageConnectionState.Faulted);

        var status = await service.ReconnectAsync();
        await WaitForAsync(() => secondSocket.SentTexts.Count >= 2);

        Assert.Equal(MessageConnectionState.Connected, status.State);
        Assert.Equal("conn-second", status.ConnectionId);
        Assert.Equal(1, status.ReconnectAttempt);
        Assert.Equal(2, negotiationClient.CallCount);

        using var replayCommand = JsonDocument.Parse(secondSocket.SentTexts[1]);
        Assert.Equal(SessionUpstreamEventNames.ReplayRequest, replayCommand.RootElement.GetProperty("event").GetString());
        var replayRequest = replayCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<ReplayRequestPayload>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(replayRequest);
        Assert.Equal(SessionMessageType.ReplayRequest, replayRequest!.MessageType);
        Assert.Equal(ReplayRequestReason.ReconnectResume, replayRequest.Payload.Reason);
        Assert.Equal(2, replayRequest.Payload.FromSequenceInclusive);
        Assert.Equal(1, replayRequest.AcknowledgedSequence);
    }

    [Fact]
    public async Task BrokerHeartbeatSendsNonceAcknowledgementUsingCurrentBrokerAck()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-heartbeat")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), socket);
        await service.PrepareForSessionAsync(session);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        await WaitForAsync(() => service.RecentTraffic.Count == 1);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerHeartbeatEnvelope(session, nonce: "nonce-123")));
        await WaitForAsync(() => socket.SentTexts.Count >= 2);

        using var heartbeatCommand = JsonDocument.Parse(socket.SentTexts[1]);
        Assert.Equal(SessionUpstreamEventNames.Heartbeat, heartbeatCommand.RootElement.GetProperty("event").GetString());
        var envelope = heartbeatCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<HeartbeatPayload>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(envelope);
        Assert.Equal(SessionMessageType.Heartbeat, envelope!.MessageType);
        Assert.Equal(MessageDirection.ClientToBroker, envelope.Direction);
        Assert.Equal(1, envelope.ClientSequence);
        Assert.Equal(1, envelope.AcknowledgedSequence);
        Assert.Equal("nonce-123", envelope.Payload.AcknowledgedNonce);
    }

    [Fact]
    public async Task BrokerHeartbeatTimeoutClosesTransportAndFaultsStatus()
    {
        var session = CreateSession();
        var clock = new MutableClock(new DateTimeOffset(2026, 03, 26, 10, 00, 00, TimeSpan.Zero));
        var delay = new ControlledDelay();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-heartbeat-timeout")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(
            new RecordingNegotiationClient(CreateNegotiationResponse(session, refreshAtUtc: DateTimeOffset.MinValue)),
            clock.GetUtcNow,
            delay.DelayAsync,
            socket);
        await service.PrepareForSessionAsync(session);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerHeartbeatEnvelope(session, nonce: "nonce-timeout", timeoutSeconds: 3)));
        await WaitForAsync(() => delay.RequestedDelays.Count == 1 && socket.SentTexts.Count >= 2);

        clock.Advance(TimeSpan.FromSeconds(4));
        delay.ReleaseNext();

        await WaitForAsync(() => service.CurrentStatus.State == MessageConnectionState.Faulted);

        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Contains("heartbeat timed out", service.CurrentStatus.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NewHeartbeatExtendsDeadlineWithoutTriggeringStaleTimeout()
    {
        var session = CreateSession();
        var clock = new MutableClock(new DateTimeOffset(2026, 03, 26, 10, 00, 00, TimeSpan.Zero));
        var delay = new ControlledDelay();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-heartbeat-extend")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(
            new RecordingNegotiationClient(CreateNegotiationResponse(session, refreshAtUtc: DateTimeOffset.MinValue)),
            clock.GetUtcNow,
            delay.DelayAsync,
            socket);
        await service.PrepareForSessionAsync(session);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerHeartbeatEnvelope(session, nonce: "nonce-initial", timeoutSeconds: 3)));
        await WaitForAsync(() => delay.RequestedDelays.Count == 1 && socket.SentTexts.Count >= 2);

        clock.Advance(TimeSpan.FromSeconds(1));
        socket.EnqueueIncoming(GroupMessage(CreateBrokerHeartbeatEnvelope(session, nonce: "nonce-refresh", timeoutSeconds: 5)));
        await WaitForAsync(() => socket.SentTexts.Count >= 3);

        clock.Advance(TimeSpan.FromSeconds(3));
        delay.ReleaseNext();
        await Task.Delay(100);

        Assert.Equal(MessageConnectionState.Connected, service.CurrentStatus.State);
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task ReconnectAsyncAfterHeartbeatTimeoutRequestsReplayFromLastAcknowledgedSequence()
    {
        var session = CreateSession();
        var clock = new MutableClock(new DateTimeOffset(2026, 03, 26, 10, 00, 00, TimeSpan.Zero));
        var delay = new ControlledDelay();
        var firstSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-heartbeat-1")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var secondSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-heartbeat-2")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(
            new RecordingNegotiationClient(CreateNegotiationResponse(session, refreshAtUtc: DateTimeOffset.MinValue)),
            clock.GetUtcNow,
            delay.DelayAsync,
            firstSocket,
            secondSocket);
        await service.PrepareForSessionAsync(session);

        firstSocket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        await WaitForAsync(() => service.RecentTraffic.Count == 1);

        firstSocket.EnqueueIncoming(GroupMessage(CreateBrokerHeartbeatEnvelope(session, nonce: "nonce-reconnect", timeoutSeconds: 3)));
        await WaitForAsync(() => delay.RequestedDelays.Count == 1 && firstSocket.SentTexts.Count >= 2);

        clock.Advance(TimeSpan.FromSeconds(4));
        delay.ReleaseNext();
        await WaitForAsync(() => service.CurrentStatus.State == MessageConnectionState.Faulted);

        var reconnectStatus = await service.ReconnectAsync();
        await WaitForAsync(() => secondSocket.SentTexts.Count >= 2);

        using var replayCommand = JsonDocument.Parse(secondSocket.SentTexts[1]);
        var replayRequest = replayCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<ReplayRequestPayload>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(replayRequest);
        Assert.Equal(MessageConnectionState.Connected, reconnectStatus.State);
        Assert.Equal(ReplayRequestReason.ReconnectResume, replayRequest!.Payload.Reason);
        Assert.Equal(2, replayRequest.Payload.FromSequenceInclusive);
        Assert.Equal(1, replayRequest.AcknowledgedSequence);
    }

    [Fact]
    public async Task HeartbeatTimeoutDuringClientRecoveryPreservesReplayCursorAcrossReconnect()
    {
        var session = CreateSession();
        var clock = new MutableClock(new DateTimeOffset(2026, 03, 26, 10, 05, 00, TimeSpan.Zero));
        var delay = new ControlledDelay();
        var firstSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-recovery-1")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var secondSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-recovery-2")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(
            new RecordingNegotiationClient(CreateNegotiationResponse(session, refreshAtUtc: DateTimeOffset.MinValue)),
            clock.GetUtcNow,
            delay.DelayAsync,
            firstSocket,
            secondSocket);

        await service.PrepareForSessionAsync(session, new MessageConnectionResumeState
        {
            Generation = 2,
            AcknowledgedSequence = 5
        });
        await WaitForAsync(() => firstSocket.SentTexts.Count >= 2);

        firstSocket.EnqueueIncoming(GroupMessage(CreateBrokerHeartbeatEnvelope(
            session,
            nonce: "nonce-recovery",
            timeoutSeconds: 3,
            generation: 2)));
        await WaitForAsync(() => delay.RequestedDelays.Count == 1 && firstSocket.SentTexts.Count >= 3);

        using (var heartbeatCommand = JsonDocument.Parse(firstSocket.SentTexts[2]))
        {
            var heartbeatEnvelope = heartbeatCommand.RootElement.GetProperty("data")
                .Deserialize<MessageEnvelope<HeartbeatPayload>>(SessionMessageSerializer.DefaultOptions);

            Assert.NotNull(heartbeatEnvelope);
            Assert.Equal(2, heartbeatEnvelope!.Generation);
            Assert.Equal(5, heartbeatEnvelope.AcknowledgedSequence);
            Assert.True(heartbeatEnvelope.Payload.ReplayRequested);
            Assert.Equal("nonce-recovery", heartbeatEnvelope.Payload.AcknowledgedNonce);
        }

        clock.Advance(TimeSpan.FromSeconds(4));
        delay.ReleaseNext();
        await WaitForAsync(() => service.CurrentStatus.State == MessageConnectionState.Faulted);

        var reconnectStatus = await service.ReconnectAsync();
        await WaitForAsync(() => secondSocket.SentTexts.Count >= 2);

        using var replayCommand = JsonDocument.Parse(secondSocket.SentTexts[1]);
        var replayRequest = replayCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<ReplayRequestPayload>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(replayRequest);
        Assert.Equal(MessageConnectionState.Connected, reconnectStatus.State);
        Assert.Equal(ReplayRequestReason.ReconnectResume, replayRequest!.Payload.Reason);
        Assert.Equal(2, replayRequest.Generation);
        Assert.Equal(5, replayRequest.AcknowledgedSequence);
        Assert.Equal(6, replayRequest.Payload.FromSequenceInclusive);
    }

    [Fact]
    public async Task PrepareForSessionAsync_WithResumeStateRequestsClientRecoveryReplay()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-restore")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), socket);
        var status = await service.PrepareForSessionAsync(session, new MessageConnectionResumeState
        {
            Generation = 2,
            AcknowledgedSequence = 5
        });
        await WaitForAsync(() => socket.SentTexts.Count >= 2);

        using var replayCommand = JsonDocument.Parse(socket.SentTexts[1]);
        var replayRequest = replayCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<ReplayRequestPayload>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(replayRequest);
        Assert.Equal(MessageConnectionState.Connected, status.State);
        Assert.True(service.CurrentStatus.IsReplayPending);
        Assert.Equal(ReplayRequestReason.ClientRecovery, service.CurrentStatus.ReplayReason);
        Assert.Equal(6, service.CurrentStatus.ReplayFromSequenceInclusive);
        Assert.Equal(ReplayRequestReason.ClientRecovery, replayRequest!.Payload.Reason);
        Assert.Equal(2, replayRequest.Generation);
        Assert.Equal(5, replayRequest.AcknowledgedSequence);
        Assert.Equal(6, replayRequest.Payload.FromSequenceInclusive);
    }

    [Fact]
    public async Task DuplicateReplayResponseEnvelopeIsIgnoredOnTheReplayPath()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-dup-replay")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), socket);
        await service.PrepareForSessionAsync(session);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 3)));
        await WaitForAsync(() => socket.SentTexts.Count >= 2);

        var replayResponse = CreateReplayResponseEnvelope(
            session,
            messageId: "replay-fixed",
            messages:
            [
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 2)),
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 3))
            ]);

        socket.EnqueueIncoming(GroupMessage(replayResponse));
        await WaitForAsync(() =>
            service.RecentTraffic.Count == 5 &&
            service.RecentTraffic.Count(traffic =>
                traffic.Direction == MessageTrafficDirection.Incoming &&
                traffic.Envelope.MessageType == SessionMessageType.ReplayResponse &&
                traffic.Envelope.MessageId == "replay-fixed") == 1);

        socket.EnqueueIncoming(GroupMessage(replayResponse));
        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 4)));
        await WaitForAsync(() =>
            service.RecentTraffic.Count == 6 &&
            service.RecentTraffic.Count(traffic =>
                traffic.Direction == MessageTrafficDirection.Incoming &&
                traffic.Envelope.MessageType == SessionMessageType.ReplayResponse &&
                traffic.Envelope.MessageId == "replay-fixed") == 1 &&
            service.RecentTraffic.Any(traffic =>
                traffic.Direction == MessageTrafficDirection.Incoming &&
                traffic.Envelope.Sequence == 4));

        await service.SendInputAsync("after-duplicate-replay");
        await WaitForAsync(() => service.RecentTraffic.Count == 7);

        using var sendCommand = JsonDocument.Parse(socket.SentTexts[2]);
        var envelope = sendCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<JsonElement>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(envelope);
        Assert.Equal(4, envelope!.AcknowledgedSequence);
        Assert.Equal(1, service.RecentTraffic.Count(traffic =>
            traffic.Direction == MessageTrafficDirection.Incoming &&
            traffic.Envelope.MessageType == SessionMessageType.ReplayResponse &&
            traffic.Envelope.MessageId == "replay-fixed"));
    }

    [Fact]
    public async Task ReplayResponseWithHasMoreRequestsAnotherPageBeforeRestoringConnectedStatus()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-paged-replay")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), socket);
        await service.PrepareForSessionAsync(session, new MessageConnectionResumeState
        {
            Generation = SessionEnvelopeContract.InitialGeneration
        });
        await WaitForAsync(() => socket.SentTexts.Count >= 2);

        socket.EnqueueIncoming(GroupMessage(CreateReplayResponseEnvelope(
            session,
            hasMore: true,
            availableFromSequence: 1,
            availableToSequence: 4,
            messages:
            [
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 1)),
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 2))
            ])));
        await WaitForAsync(() => socket.SentTexts.Count >= 3);

        using (var secondReplayCommand = JsonDocument.Parse(socket.SentTexts[2]))
        {
            var secondReplayRequest = secondReplayCommand.RootElement.GetProperty("data")
                .Deserialize<MessageEnvelope<ReplayRequestPayload>>(SessionMessageSerializer.DefaultOptions);

            Assert.NotNull(secondReplayRequest);
            Assert.Equal(ReplayRequestReason.ClientRecovery, secondReplayRequest!.Payload.Reason);
            Assert.Equal(3, secondReplayRequest.Payload.FromSequenceInclusive);
            Assert.Equal(4, secondReplayRequest.Payload.ToSequenceInclusive);
        }

        socket.EnqueueIncoming(GroupMessage(CreateReplayResponseEnvelope(
            session,
            availableFromSequence: 1,
            availableToSequence: 4,
            messages:
            [
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 3)),
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 4))
            ])));
        await WaitForAsync(() => service.CurrentStatus.State == MessageConnectionState.Connected && !service.CurrentStatus.IsReplayPending);

        Assert.Equal(4, service.CurrentStatus.AcknowledgedSequence);
        Assert.Contains(service.RecentTraffic, traffic => traffic.Direction == MessageTrafficDirection.Incoming && traffic.Envelope.Sequence == 4);
    }

    [Fact]
    public async Task ReplayResponseWithHasMoreAndEmptyPageRequestsAnotherPageBeforeRestoringConnectedStatus()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-empty-page-replay")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), socket);
        await service.PrepareForSessionAsync(session, new MessageConnectionResumeState
        {
            Generation = SessionEnvelopeContract.InitialGeneration
        });
        await WaitForAsync(() => socket.SentTexts.Count >= 2);

        socket.EnqueueIncoming(GroupMessage(CreateReplayResponseEnvelope(
            session,
            hasMore: true,
            availableFromSequence: 1,
            availableToSequence: 4,
            messages: [])));
        await WaitForAsync(() => socket.SentTexts.Count >= 3);

        using (var secondReplayCommand = JsonDocument.Parse(socket.SentTexts[2]))
        {
            var secondReplayRequest = secondReplayCommand.RootElement.GetProperty("data")
                .Deserialize<MessageEnvelope<ReplayRequestPayload>>(SessionMessageSerializer.DefaultOptions);

            Assert.NotNull(secondReplayRequest);
            Assert.Equal(ReplayRequestReason.ClientRecovery, secondReplayRequest!.Payload.Reason);
            Assert.Equal(1, secondReplayRequest.Payload.FromSequenceInclusive);
            Assert.Equal(4, secondReplayRequest.Payload.ToSequenceInclusive);
        }

        Assert.True(service.CurrentStatus.IsReplayPending);

        socket.EnqueueIncoming(GroupMessage(CreateReplayResponseEnvelope(
            session,
            availableFromSequence: 1,
            availableToSequence: 4,
            messages:
            [
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 1)),
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 2)),
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 3)),
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 4))
            ])));
        await WaitForAsync(() => service.CurrentStatus.State == MessageConnectionState.Connected && !service.CurrentStatus.IsReplayPending);

        Assert.Equal(4, service.CurrentStatus.AcknowledgedSequence);
        Assert.Contains(service.RecentTraffic, traffic => traffic.Direction == MessageTrafficDirection.Incoming && traffic.Envelope.Sequence == 4);
    }

    [Fact]
    public async Task PrepareForSessionAsyncSchedulesTokenRefreshUsingFunctionRefreshWindow()
    {
        var session = CreateSession();
        var now = new DateTimeOffset(2026, 03, 25, 18, 00, 00, TimeSpan.Zero);
        var delay = new ControlledDelay();
        var firstSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-refresh-1")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var secondSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-refresh-2")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var negotiationClient = new ScriptedNegotiationClient(
            CreateNegotiationResponse(session, now.AddMinutes(60)),
            CreateNegotiationResponse(session, now.AddMinutes(120)));

        await using var service = CreateService(
            negotiationClient,
            () => now,
            delay.DelayAsync,
            firstSocket,
            secondSocket);

        await service.PrepareForSessionAsync(session);

        Assert.Equal(TimeSpan.FromMinutes(60), Assert.Single(delay.RequestedDelays));

        delay.ReleaseNext();

        await WaitForAsync(() =>
            negotiationClient.CallCount == 2 &&
            service.CurrentStatus.State == MessageConnectionState.Connected &&
            service.CurrentStatus.ConnectionId == "conn-refresh-2");

        await WaitForAsync(() => delay.RequestedDelays.Count >= 2);

        Assert.Equal(TimeSpan.FromMinutes(120), delay.RequestedDelays[1]);
    }

    [Fact]
    public async Task TokenRefreshReconnectKeepsAcknowledgedSequenceHealthy()
    {
        var session = CreateSession();
        var now = new DateTimeOffset(2026, 03, 25, 18, 00, 00, TimeSpan.Zero);
        var delay = new ControlledDelay();
        var firstSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-live-1")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var secondSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-live-2")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var negotiationClient = new ScriptedNegotiationClient(
            CreateNegotiationResponse(session, now.AddMinutes(60)),
            CreateNegotiationResponse(session, now.AddMinutes(120)));

        await using var service = CreateService(
            negotiationClient,
            () => now,
            delay.DelayAsync,
            firstSocket,
            secondSocket);

        await service.PrepareForSessionAsync(session);

        firstSocket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        await WaitForAsync(() => service.RecentTraffic.Count == 1);

        await WaitForAsync(() => delay.RequestedDelays.Count == 1);
        delay.ReleaseNext();
        await WaitForAsync(() => service.CurrentStatus.ConnectionId == "conn-live-2");

        secondSocket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 2)));
        await WaitForAsync(() => service.RecentTraffic.Count >= 3);

        await service.SendInputAsync("after-refresh");
        await WaitForAsync(() => service.RecentTraffic.Count >= 4);

        using var sendCommand = JsonDocument.Parse(secondSocket.SentTexts[^1]);
        var envelope = sendCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<JsonElement>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(envelope);
        Assert.Equal(2, envelope!.AcknowledgedSequence);
        Assert.Equal(1, envelope.ClientSequence);
    }

    [Fact]
    public async Task TokenRefreshRejoinAcceptsNewGenerationBoundaryAndResetsClientAck()
    {
        var session = CreateSession();
        var now = new DateTimeOffset(2026, 03, 25, 18, 10, 00, TimeSpan.Zero);
        var delay = new ControlledDelay();
        var firstSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-refresh-generation-1")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var secondSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-refresh-generation-2")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var negotiationClient = new ScriptedNegotiationClient(
            CreateNegotiationResponse(session, now.AddMinutes(60)),
            CreateNegotiationResponse(session, now.AddMinutes(120)));

        await using var service = CreateService(
            negotiationClient,
            () => now,
            delay.DelayAsync,
            firstSocket,
            secondSocket);

        await service.PrepareForSessionAsync(session);

        firstSocket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        await WaitForAsync(() => service.RecentTraffic.Count == 1);

        await WaitForAsync(() => delay.RequestedDelays.Count == 1);
        delay.ReleaseNext();
        await WaitForAsync(() => secondSocket.SentTexts.Count >= 2 && service.CurrentStatus.ConnectionId == "conn-refresh-generation-2");

        secondSocket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1, generation: 2, content: "after-refresh-generation")));
        await WaitForAsync(() => service.RecentTraffic.Any(traffic =>
            traffic.Direction == MessageTrafficDirection.Incoming &&
            traffic.Envelope.Generation == 2 &&
            traffic.Envelope.Sequence == 1));

        await service.SendInputAsync("after-refresh-generation");
        await WaitForAsync(() => service.RecentTraffic.Count >= 4);

        using var sendCommand = JsonDocument.Parse(secondSocket.SentTexts[^1]);
        var envelope = sendCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<JsonElement>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(envelope);
        Assert.Equal(2, envelope!.Generation);
        Assert.Equal(1, envelope.AcknowledgedSequence);
        Assert.Equal(1, envelope.ClientSequence);
    }

    [Fact]
    public async Task TokenRefreshRetriesNegotiateFailuresBeforeRejoiningSession()
    {
        var session = CreateSession();
        var now = new DateTimeOffset(2026, 03, 25, 18, 00, 00, TimeSpan.Zero);
        var delay = new ControlledDelay();
        var firstSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-refresh-retry-1")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var secondSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-refresh-retry-2")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var negotiationClient = new ScriptedNegotiationClient(
            CreateNegotiationResponse(session, refreshAtUtc: now.AddMinutes(10), expiresAtUtc: now.AddMinutes(20)),
            new InvalidOperationException("Negotiate failed with 503 (ServiceUnavailable)."),
            new InvalidOperationException("Negotiate failed with 503 (ServiceUnavailable)."),
            CreateNegotiationResponse(session, refreshAtUtc: now.AddMinutes(30), expiresAtUtc: now.AddMinutes(40)));

        await using var service = CreateService(
            negotiationClient,
            () => now,
            delay.DelayAsync,
            firstSocket,
            secondSocket);

        await service.PrepareForSessionAsync(session);

        await WaitForAsync(() => delay.RequestedDelays.Count == 1);
        delay.ReleaseNext();

        await WaitForAsync(() => delay.RequestedDelays.Count == 2);
        Assert.Equal(MessageConnectionState.Connected, service.CurrentStatus.State);
        Assert.Equal(1, service.CurrentStatus.RefreshAttempt);
        Assert.Contains("Retrying in 5 second", service.CurrentStatus.Summary, StringComparison.OrdinalIgnoreCase);

        delay.ReleaseNext();
        await WaitForAsync(() => delay.RequestedDelays.Count == 3);
        Assert.Equal(MessageConnectionState.Connected, service.CurrentStatus.State);
        Assert.Equal(2, service.CurrentStatus.RefreshAttempt);
        Assert.Contains("Retrying in 10 second", service.CurrentStatus.Summary, StringComparison.OrdinalIgnoreCase);

        delay.ReleaseNext();
        await WaitForAsync(() =>
            negotiationClient.CallCount == 4 &&
            service.CurrentStatus.State == MessageConnectionState.Connected &&
            service.CurrentStatus.ConnectionId == "conn-refresh-retry-2");

        Assert.Equal(0, service.CurrentStatus.RefreshAttempt);
    }

    [Fact]
    public async Task ReplayDiagnosticsTrafficPreservesOrderingContextAndRedactsSecrets()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-diagnostics")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), socket);
        await service.PrepareForSessionAsync(session);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(
            session,
            sequence: 1,
            content: "password=swordfish")));
        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(
            session,
            sequence: 3,
            content: "ghp_1234567890abcdef")));
        await WaitForAsync(() =>
            socket.SentTexts.Count >= 2 &&
            service.RecentTraffic.Any(traffic =>
                traffic.Direction == MessageTrafficDirection.Outgoing &&
                traffic.Envelope.MessageType == SessionMessageType.ReplayRequest));

        using var replayCommand = JsonDocument.Parse(socket.SentTexts[1]);
        Assert.Equal(SessionUpstreamEventNames.ReplayRequest, replayCommand.RootElement.GetProperty("event").GetString());
        var replayRequest = replayCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<ReplayRequestPayload>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(replayRequest);

        socket.EnqueueIncoming(GroupMessage(CreateReplayResponseEnvelope(
            session,
            correlationId: replayRequest!.CorrelationId,
            causationId: replayRequest.MessageId,
            availableFromSequence: 1,
            availableToSequence: 3,
            messages:
            [
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 2, content: "token=resume-secret")),
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 3, content: "https://user:pass@example.com/path?sig=abc"))
            ])));
        await WaitForAsync(() => service.RecentTraffic.Any(traffic => traffic.Envelope.MessageType == SessionMessageType.ReplayResponse));

        Assert.Collection(
            service.RecentTraffic.Take(3),
            traffic => Assert.Equal(1, traffic.Envelope.Sequence),
            traffic => Assert.Equal(3, traffic.Envelope.Sequence),
            traffic =>
            {
                Assert.Equal(MessageTrafficDirection.Outgoing, traffic.Direction);
                Assert.Equal(SessionMessageType.ReplayRequest, traffic.Envelope.MessageType);
            });

        var replayDiagnostics = Assert.Single(
            service.RecentTraffic,
            traffic => traffic.Envelope.MessageType == SessionMessageType.ReplayResponse);
        var replayPayload = replayDiagnostics.Envelope.Payload.Deserialize<ReplayResponsePayload>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(replayPayload);
        Assert.Equal(replayRequest.CorrelationId, replayDiagnostics.Envelope.CorrelationId);
        Assert.Equal(replayRequest.MessageId, replayDiagnostics.Envelope.CausationId);
        Assert.Equal(SessionEnvelopeContract.InitialGeneration, replayDiagnostics.Envelope.Generation);
        Assert.Equal(1, replayPayload!.AvailableFromSequence);
        Assert.Equal(3, replayPayload.AvailableToSequence);
        Assert.False(replayPayload.GapDetected);
        Assert.Equal(2, replayPayload.Messages.Count);

        var diagnosticPayloads = string.Join(
            Environment.NewLine,
            service.RecentTraffic.Select(traffic => traffic.Envelope.Payload.GetRawText()));
        Assert.DoesNotContain("swordfish", diagnosticPayloads, StringComparison.Ordinal);
        Assert.DoesNotContain("resume-secret", diagnosticPayloads, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", diagnosticPayloads, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_1234567890abcdef", diagnosticPayloads, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.RedactedValue, diagnosticPayloads, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecentTrafficHonorsConfiguredCapacityForLocalDiagnostics()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-capacity")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(
            new MessagingOptions
            {
                Hub = "squadscout",
                NegotiateUrl = "http://127.0.0.1:7071/api/negotiate",
                ConnectTimeoutSeconds = 5,
                CommandAckTimeoutSeconds = 5,
                RecentTrafficCapacity = 2
            },
            new RecordingNegotiationClient(CreateNegotiationResponse(session)),
            static () => DateTimeOffset.UtcNow,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            socket);
        await service.PrepareForSessionAsync(session);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 2)));
        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 3)));
        await WaitForAsync(() =>
            service.RecentTraffic.Count == 2 &&
            service.RecentTraffic[0].Envelope.Sequence == 2 &&
            service.RecentTraffic[1].Envelope.Sequence == 3);

        Assert.Equal(2, service.RecentTraffic.Count);
        Assert.Collection(
            service.RecentTraffic,
            traffic => Assert.Equal(2, traffic.Envelope.Sequence),
            traffic => Assert.Equal(3, traffic.Envelope.Sequence));
    }

    [Fact]
    public async Task TokenRefreshFailureTransitionsToFaultedAfterConfiguredRetries()
    {
        var session = CreateSession();
        var now = new DateTimeOffset(2026, 03, 25, 18, 00, 00, TimeSpan.Zero);
        var delay = new ControlledDelay();
        var firstSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-fault-1")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var messagingOptions = new MessagingOptions
        {
            Hub = "squadscout",
            NegotiateUrl = "http://127.0.0.1:7071/api/negotiate",
            ConnectTimeoutSeconds = 5,
            CommandAckTimeoutSeconds = 5,
            TokenRefreshRetryCount = 1,
            TokenRefreshRetryBaseDelaySeconds = 5,
            TokenRefreshRetryMaxDelaySeconds = 60,
            RecentTrafficCapacity = 20
        };

        var negotiationClient = new ScriptedNegotiationClient(
            CreateNegotiationResponse(session, refreshAtUtc: now.AddMinutes(10), expiresAtUtc: now.AddMinutes(20)),
            new InvalidOperationException("Negotiate failed with 401 (Unauthorized). The negotiate endpoint requires a trusted identity."),
            new InvalidOperationException("Negotiate failed with 401 (Unauthorized). The negotiate endpoint requires a trusted identity."));

        await using var service = CreateService(
            messagingOptions,
            negotiationClient,
            () => now,
            delay.DelayAsync,
            firstSocket);

        await service.PrepareForSessionAsync(session);

        await WaitForAsync(() => delay.RequestedDelays.Count == 1);
        delay.ReleaseNext();
        await WaitForAsync(() => delay.RequestedDelays.Count == 2);
        Assert.Equal(MessageConnectionState.Connected, service.CurrentStatus.State);
        Assert.Equal(1, service.CurrentStatus.RefreshAttempt);

        delay.ReleaseNext();

        await WaitForAsync(() => service.CurrentStatus.State == MessageConnectionState.Faulted);

        Assert.Equal(3, negotiationClient.CallCount);
        Assert.Contains("Token refresh failed", service.CurrentStatus.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Token refresh failed", service.CurrentStatus.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("after 2 attempts", service.CurrentStatus.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retry the live transport", service.CurrentStatus.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trusted identity", service.CurrentStatus.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TokenRefreshAuthDriftFaultsBeforeRejoiningWrongSessionIdentity()
    {
        var session = CreateSession();
        var now = new DateTimeOffset(2026, 03, 25, 18, 00, 00, TimeSpan.Zero);
        var delay = new ControlledDelay();
        var firstSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-auth-drift")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var negotiationClient = new ScriptedNegotiationClient(
            CreateNegotiationResponse(session, refreshAtUtc: now.AddMinutes(10), expiresAtUtc: now.AddMinutes(20)),
            CreateNegotiationResponse(
                session,
                refreshAtUtc: now.AddMinutes(30),
                expiresAtUtc: now.AddMinutes(40),
                userId: $"client:{session.ProjectId}:{session.SessionId}:other-user"));

        await using var service = CreateService(
            negotiationClient,
            () => now,
            delay.DelayAsync,
            firstSocket);

        await service.PrepareForSessionAsync(session);

        await WaitForAsync(() => delay.RequestedDelays.Count == 1);
        delay.ReleaseNext();

        await WaitForAsync(() => service.CurrentStatus.State == MessageConnectionState.Faulted);

        Assert.Contains("authentication drift", service.CurrentStatus.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("other-user", service.CurrentStatus.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WebSocketState.Closed, firstSocket.State);
    }

    [Fact]
    public async Task PrepareForSessionAsyncReturnsFaultedStatusWhenNegotiateFails()
    {
        var session = CreateSession();
        var service = CreateService(
            new ThrowingNegotiationClient("Negotiate failed with 401 (Unauthorized). The negotiate endpoint requires a trusted identity."),
            new FakeWebPubSubSocket());

        var status = await service.PrepareForSessionAsync(session);

        Assert.Equal(MessageConnectionState.Faulted, status.State);
        Assert.Contains("trusted identity", status.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trusted identity", status.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PubSubNegotiationClientAddsDevelopmentHeadersForLoopbackNegotiation()
    {
        HttpRequestMessage? capturedRequest = null;
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    CreateNegotiationResponse(CreateSession()),
                    options: SessionMessageSerializer.DefaultOptions)
            });
        }));

        var client = new PubSubNegotiationClient(
            httpClient,
            new MessagingOptions
            {
                NegotiateUrl = "http://127.0.0.1:7071/api/negotiate"
            },
            new ConfiguredAuthenticationService(new AuthOptions
            {
                Mode = "LocalDevelopment",
                DefaultRequestedBy = "seraph@local"
            }));

        await client.NegotiateAsync(CreateSession());

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Headers.TryGetValues("x-squadscout-dev-user", out var users));
        Assert.Equal("seraph@local", Assert.Single(users));
        Assert.True(capturedRequest.Headers.TryGetValues("x-squadscout-dev-name", out var displayNames));
        Assert.Equal("seraph@local", Assert.Single(displayNames));
    }

    [Fact]
    public async Task PubSubNegotiationClientRejectsMismatchedScopedResponses()
    {
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    CreateNegotiationResponse(
                        CreateSession(),
                        userId: "client:proj-01:session-other:mobile-user") with
                    {
                        SessionId = "session-other"
                    },
                    options: SessionMessageSerializer.DefaultOptions)
            })));

        var client = new PubSubNegotiationClient(
            httpClient,
            new MessagingOptions
            {
                NegotiateUrl = "http://127.0.0.1:7071/api/negotiate"
            },
            new ConfiguredAuthenticationService(new AuthOptions
            {
                Mode = "LocalDevelopment",
                DefaultRequestedBy = "seraph@local"
            }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.NegotiateAsync(CreateSession()));

        Assert.Contains("session-other", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("session-abc", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReceivingBrokerMessagesWithSequenceGapSetsGapDetectedStatus()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-gap")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), socket);
        await service.PrepareForSessionAsync(session);

        // Deliver sequence 1, then skip 2 and deliver 3 to produce a gap.
        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        await WaitForAsync(() => service.RecentTraffic.Count == 1);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 3)));
        await WaitForAsync(() =>
            socket.SentTexts.Count >= 2 &&
            service.CurrentStatus.Summary.Contains("gap", StringComparison.OrdinalIgnoreCase));

        using var replayCommand = JsonDocument.Parse(socket.SentTexts[1]);
        var replayRequest = replayCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<ReplayRequestPayload>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(replayRequest);
        Assert.Equal(SessionMessageType.ReplayRequest, replayRequest!.MessageType);
        Assert.Equal(ReplayRequestReason.GapDetected, replayRequest.Payload.Reason);
        Assert.Equal(2, replayRequest.Payload.FromSequenceInclusive);
        Assert.Equal(SessionEnvelopeContract.InitialGeneration, replayRequest.Generation);
        Assert.Equal(1, replayRequest.AcknowledgedSequence);
    }

    [Fact]
    public async Task ReplayResponseGapDetectedSurfacesDurableWarning()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-replay-gap")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), socket);
        await service.PrepareForSessionAsync(session);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        await WaitForAsync(() => service.RecentTraffic.Count == 1);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 3)));
        await WaitForAsync(() => socket.SentTexts.Count >= 2);

        socket.EnqueueIncoming(GroupMessage(CreateReplayResponseEnvelope(
            session,
            gapDetected: true,
            availableFromSequence: 5,
            availableToSequence: 12)));
        await WaitForAsync(() => service.CurrentStatus.Summary.Contains("replay gap", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(MessageConnectionState.Faulted, service.CurrentStatus.State);
        Assert.Contains("replay gap", service.CurrentStatus.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trusting transcript continuity", service.CurrentStatus.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconnectAsyncAfterReplayGapFaultRequestsReplayFromLastAcknowledgedSequence()
    {
        var session = CreateSession();
        var firstSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-gap-1")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var secondSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-gap-2")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), firstSocket, secondSocket);
        await service.PrepareForSessionAsync(session);

        firstSocket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        firstSocket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 3)));
        await WaitForAsync(() => firstSocket.SentTexts.Count >= 2);

        firstSocket.EnqueueIncoming(GroupMessage(CreateReplayResponseEnvelope(
            session,
            gapDetected: true,
            availableFromSequence: 5,
            availableToSequence: 12)));
        await WaitForAsync(() => service.CurrentStatus.State == MessageConnectionState.Faulted);

        var reconnectStatus = await service.ReconnectAsync();
        await WaitForAsync(() => secondSocket.SentTexts.Count >= 2);

        using var replayCommand = JsonDocument.Parse(secondSocket.SentTexts[1]);
        var replayRequest = replayCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<ReplayRequestPayload>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(replayRequest);
        Assert.Equal(MessageConnectionState.Connected, reconnectStatus.State);
        Assert.Equal(ReplayRequestReason.ReconnectResume, replayRequest!.Payload.Reason);
        Assert.Equal(SessionEnvelopeContract.InitialGeneration, replayRequest.Generation);
        Assert.Equal(1, replayRequest.AcknowledgedSequence);
        Assert.Equal(2, replayRequest.Payload.FromSequenceInclusive);
    }

    [Fact]
    public async Task ReceivingNewGenerationResetsClientAcknowledgementState()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-gen")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), socket);
        await service.PrepareForSessionAsync(session);

        // Establish acknowledged state at generation 1.
        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 2)));
        await WaitForAsync(() => service.RecentTraffic.Count == 2);

        // Deliver a message from generation 2 (simulates broker PTY restart).
        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1, generation: 2)));
        await WaitForAsync(() => service.RecentTraffic.Count == 3);

        // After the generation reset, the next input must echo generation 2 and ack sequence 1 (not 2).
        await service.SendInputAsync("hello");
        await WaitForAsync(() => service.RecentTraffic.Count == 4);

        using var sendCommand = JsonDocument.Parse(socket.SentTexts[1]);
        var envelope = sendCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<JsonElement>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(envelope);
        Assert.Equal(2, envelope!.Generation);
        Assert.Equal(1, envelope.AcknowledgedSequence);
    }

    [Fact]
    public async Task ReceivingLowerGenerationEnvelopeFaultsWithoutRollingBackGeneration()
    {
        var session = CreateSession();
        var firstSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-stale-output-1")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        var secondSocket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-stale-output-2")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), firstSocket, secondSocket);
        await service.PrepareForSessionAsync(session);

        firstSocket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        firstSocket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 2)));
        firstSocket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1, generation: 2)));
        await WaitForAsync(() => service.RecentTraffic.Count == 3);

        firstSocket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 99, generation: 1, content: "stale-output")));
        await WaitForAsync(() => service.CurrentStatus.State == MessageConnectionState.Faulted && service.RecentTraffic.Count == 4);

        Assert.NotNull(service.CurrentStatus.FailureReason);
        Assert.Contains("stale generation 1", service.CurrentStatus.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already on generation 2", service.CurrentStatus.FailureReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "generation 1 is older than active generation 2",
            service.RecentTraffic[^1].Summary,
            StringComparison.OrdinalIgnoreCase);

        firstSocket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 2, generation: 2, content: "should-not-apply")));
        await Task.Delay(100);
        Assert.Equal(4, service.RecentTraffic.Count);

        var reconnectStatus = await service.ReconnectAsync();
        await WaitForAsync(() => secondSocket.SentTexts.Count >= 2);

        Assert.Equal(MessageConnectionState.Connected, reconnectStatus.State);

        using var replayCommand = JsonDocument.Parse(secondSocket.SentTexts[1]);
        var replayRequest = replayCommand.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<ReplayRequestPayload>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(replayRequest);
        Assert.Equal(ReplayRequestReason.ReconnectResume, replayRequest!.Payload.Reason);
        Assert.Equal(2, replayRequest.Generation);
        Assert.Equal(1, replayRequest.AcknowledgedSequence);
    }

    [Fact]
    public async Task ReplayResponseFromLowerGenerationIsRejectedBeforeApplyingReplayMessages()
    {
        var session = CreateSession();
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", connectionId: "conn-stale-replay")
            ],
            OnSendAsync = command =>
            {
                var json = JsonDocument.Parse(command);
                var ackId = json.RootElement.GetProperty("ackId").GetInt64();
                return Task.FromResult<string?>(AckMessage(ackId, success: true));
            }
        };

        await using var service = CreateService(new RecordingNegotiationClient(CreateNegotiationResponse(session)), socket);
        await service.PrepareForSessionAsync(session);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1)));
        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 2)));
        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 1, generation: 2)));
        await WaitForAsync(() => service.RecentTraffic.Count == 3);

        socket.EnqueueIncoming(GroupMessage(CreateReplayResponseEnvelope(
            session,
            generation: 1,
            messages:
            [
                ToJsonEnvelope(CreateBrokerEnvelope(session, sequence: 3, generation: 1, content: "stale-replay"))
            ])));
        await WaitForAsync(() => service.CurrentStatus.State == MessageConnectionState.Faulted && service.RecentTraffic.Count == 4);

        var replayResponse = Assert.Single(
            service.RecentTraffic,
            traffic => traffic.Envelope.MessageType == SessionMessageType.ReplayResponse);

        Assert.NotNull(service.CurrentStatus.FailureReason);
        Assert.Contains("Rejected incoming ReplayResponse envelope", replayResponse.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stale generation 1", service.CurrentStatus.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already on generation 2", service.CurrentStatus.FailureReason!, StringComparison.OrdinalIgnoreCase);

        socket.EnqueueIncoming(GroupMessage(CreateBrokerEnvelope(session, sequence: 2, generation: 2, content: "should-not-apply")));
        await Task.Delay(100);
        Assert.Equal(4, service.RecentTraffic.Count);

        Assert.DoesNotContain(
            service.RecentTraffic,
            traffic => traffic.Summary.Contains("Applied replayed", StringComparison.OrdinalIgnoreCase));
    }

    private static SessionDescriptor CreateSession() =>
        new()
        {
            ProjectId = "proj-01",
            SessionId = "session-abc",
            State = SessionState.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

    private static PubSubNegotiateResponse CreateNegotiationResponse(
        SessionDescriptor session,
        DateTimeOffset? refreshAtUtc = null,
        DateTimeOffset? expiresAtUtc = null,
        string? userId = null,
        string? sessionGroup = null,
        string? hub = null) =>
        new()
        {
            Url = "wss://example.webpubsub.azure.com/client/hubs/squadscout?access_token=test",
            Hub = hub ?? "squadscout",
            UserId = userId ?? $"client:{session.ProjectId}:{session.SessionId}:mobile-user",
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            ParticipantKind = PubSubParticipantKind.Client,
            SessionGroup = sessionGroup ?? $"session:{session.ProjectId}:{session.SessionId}",
            ExpiresAtUtc = expiresAtUtc ?? DateTimeOffset.UtcNow.AddHours(1),
            RefreshAtUtc = refreshAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(50)
        };

    private static MessageEnvelope<OutputChunkPayload> CreateBrokerEnvelope(
        SessionDescriptor session,
        long sequence,
        long generation = SessionEnvelopeContract.InitialGeneration,
        string content = "ready",
        string? correlationId = null) =>
        new()
        {
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            Generation = generation,
            MessageType = SessionMessageType.Output,
            Direction = MessageDirection.BrokerToClient,
            Sequence = sequence,
            TimestampUtc = DateTimeOffset.UtcNow,
            MessageId = $"broker-{sequence}",
            CorrelationId = correlationId ?? $"broker-session:{session.SessionId}",
            Payload = new OutputChunkPayload
            {
                Content = content,
                IsError = false
            }
        };

    private static MessageEnvelope<HeartbeatPayload> CreateBrokerHeartbeatEnvelope(
        SessionDescriptor session,
        string nonce,
        int timeoutSeconds = SessionHeartbeatDefaults.LivenessTimeoutSeconds,
        int expectedIntervalSeconds = SessionHeartbeatDefaults.ExpectedIntervalSeconds,
        long generation = SessionEnvelopeContract.InitialGeneration) =>
        new()
        {
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            Generation = generation,
            MessageType = SessionMessageType.Heartbeat,
            Direction = MessageDirection.BrokerToClient,
            TimestampUtc = DateTimeOffset.UtcNow,
            MessageId = $"heartbeat-{nonce}",
            CorrelationId = $"broker-session:{session.SessionId}",
            Payload = new HeartbeatPayload
            {
                ExpectedIntervalSeconds = expectedIntervalSeconds,
                LivenessTimeoutSeconds = timeoutSeconds,
                SenderInstanceId = "broker-tests",
                Nonce = nonce
            }
        };

    private static MessageEnvelope<ReplayResponsePayload> CreateReplayResponseEnvelope(
        SessionDescriptor session,
        long generation = SessionEnvelopeContract.InitialGeneration,
        bool gapDetected = false,
        bool hasMore = false,
        long? availableFromSequence = null,
        long? availableToSequence = null,
        IReadOnlyList<MessageEnvelope<JsonElement>>? messages = null,
        string? messageId = null,
        string? correlationId = null,
        string? causationId = null) =>
        new()
        {
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            Generation = generation,
            MessageType = SessionMessageType.ReplayResponse,
            Direction = MessageDirection.BrokerToClient,
            TimestampUtc = DateTimeOffset.UtcNow,
            MessageId = messageId ?? $"replay-{Guid.NewGuid():n}",
            CorrelationId = correlationId ?? $"broker-session:{session.SessionId}",
            CausationId = causationId,
            Payload = new ReplayResponsePayload
            {
                Generation = generation,
                AvailableFromSequence = availableFromSequence,
                AvailableToSequence = availableToSequence,
                GapDetected = gapDetected,
                HasMore = hasMore,
                IsComplete = !hasMore,
                Messages = messages ?? Array.Empty<MessageEnvelope<JsonElement>>()
            }
        };

    private static MessageEnvelope<JsonElement> ToJsonEnvelope<TPayload>(MessageEnvelope<TPayload> envelope) =>
        new()
        {
            ContractVersion = envelope.ContractVersion,
            ProjectId = envelope.ProjectId,
            SessionId = envelope.SessionId,
            Generation = envelope.Generation,
            MessageType = envelope.MessageType,
            Direction = envelope.Direction,
            Sequence = envelope.Sequence,
            ClientSequence = envelope.ClientSequence,
            AcknowledgedSequence = envelope.AcknowledgedSequence,
            TimestampUtc = envelope.TimestampUtc,
            MessageId = envelope.MessageId,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            Payload = envelope.Payload is JsonElement payload
                ? payload.Clone()
                : JsonSerializer.SerializeToElement(envelope.Payload, SessionMessageSerializer.DefaultOptions)
        };

    private static string SystemMessage(string @event, string? connectionId = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "system",
            ["event"] = @event
        };

        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            payload["connectionId"] = connectionId;
        }

        return JsonSerializer.Serialize(payload, SessionMessageSerializer.DefaultOptions);
    }

    private static string AckMessage(long ackId, bool success) =>
        JsonSerializer.Serialize(
            new
            {
                type = "ack",
                ackId,
                success
            },
            SessionMessageSerializer.DefaultOptions);

    private static string GroupMessage<TPayload>(MessageEnvelope<TPayload> envelope) =>
        JsonSerializer.Serialize(
            new
            {
                type = "message",
                from = "group",
                group = $"session:{envelope.ProjectId}:{envelope.SessionId}",
                dataType = "json",
                data = envelope
            },
            SessionMessageSerializer.DefaultOptions);

    private static MessagingConnectionService CreateService(
        IPubSubNegotiationClient negotiationClient,
        params FakeWebPubSubSocket[] sockets) =>
        CreateService(
            negotiationClient,
            static () => DateTimeOffset.UtcNow,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            sockets);

    private static MessagingConnectionService CreateService(
        MessagingOptions messagingOptions,
        IPubSubNegotiationClient negotiationClient,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        params FakeWebPubSubSocket[] sockets) =>
        new(
            messagingOptions,
            negotiationClient,
            new FakeWebPubSubSocketFactory(sockets),
            utcNow,
            delayAsync);

    private static MessagingConnectionService CreateService(
        IPubSubNegotiationClient negotiationClient,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        params FakeWebPubSubSocket[] sockets) =>
        CreateService(
            new MessagingOptions
            {
                Hub = "squadscout",
                NegotiateUrl = "http://127.0.0.1:7071/api/negotiate",
                ConnectTimeoutSeconds = 5,
                CommandAckTimeoutSeconds = 5,
                RecentTrafficCapacity = 20
            },
            negotiationClient,
            utcNow,
            delayAsync,
            sockets);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("The expected asynchronous condition was not satisfied before the test timed out.");
    }

    private sealed class RecordingNegotiationClient : IPubSubNegotiationClient
    {
        private readonly PubSubNegotiateResponse _response;

        public RecordingNegotiationClient(PubSubNegotiateResponse response)
        {
            _response = response;
        }

        public int CallCount { get; private set; }

        public Task<PubSubNegotiateResponse> NegotiateAsync(SessionDescriptor session, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_response with
            {
                ProjectId = session.ProjectId,
                SessionId = session.SessionId,
                SessionGroup = $"session:{session.ProjectId}:{session.SessionId}"
            });
        }
    }

    private sealed class ThrowingNegotiationClient : IPubSubNegotiationClient
    {
        private readonly string _message;

        public ThrowingNegotiationClient(string message)
        {
            _message = message;
        }

        public Task<PubSubNegotiateResponse> NegotiateAsync(SessionDescriptor session, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(_message);
    }

    private sealed class ScriptedNegotiationClient : IPubSubNegotiationClient
    {
        private readonly Queue<object> _steps;

        public ScriptedNegotiationClient(params object[] steps)
        {
            _steps = new Queue<object>(steps);
        }

        public int CallCount { get; private set; }

        public Task<PubSubNegotiateResponse> NegotiateAsync(SessionDescriptor session, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("No scripted negotiate response was queued for the token refresh test.");
            }

            var step = _steps.Dequeue();
            return step switch
            {
                PubSubNegotiateResponse response => Task.FromResult(response with
                {
                    ProjectId = session.ProjectId,
                    SessionId = session.SessionId,
                    SessionGroup = $"session:{session.ProjectId}:{session.SessionId}"
                }),
                Exception exception => Task.FromException<PubSubNegotiateResponse>(exception),
                _ => throw new InvalidOperationException($"Unsupported scripted negotiate step '{step.GetType().Name}'.")
            };
        }
    }

    private sealed class ControlledDelay
    {
        private readonly Queue<TaskCompletionSource<bool>> _pendingDelays = new();

        public List<TimeSpan> RequestedDelays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            RequestedDelays.Add(delay);

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingDelays.Enqueue(tcs);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }

        public void ReleaseNext()
        {
            if (_pendingDelays.Count == 0)
            {
                throw new InvalidOperationException("No token refresh delay is currently pending.");
            }

            _pendingDelays.Dequeue().TrySetResult(true);
        }
    }

    private sealed class MutableClock
    {
        private readonly object _sync = new();
        private DateTimeOffset _utcNow;

        public MutableClock(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _utcNow;
            }
        }

        public void Advance(TimeSpan delta)
        {
            lock (_sync)
            {
                _utcNow = _utcNow.Add(delta);
            }
        }
    }

    private sealed class FakeWebPubSubSocketFactory : IWebPubSubSocketFactory
    {
        private readonly Queue<FakeWebPubSubSocket> _sockets;

        public FakeWebPubSubSocketFactory(params FakeWebPubSubSocket[] sockets)
        {
            _sockets = new Queue<FakeWebPubSubSocket>(sockets);
        }

        public IWebPubSubSocket Create() => _sockets.Dequeue();
    }

    private sealed class FakeWebPubSubSocket : IWebPubSubSocket
    {
        private readonly Queue<string?> _incoming = new();
        private readonly SemaphoreSlim _incomingSignal = new(0, int.MaxValue);

        public IReadOnlyList<string> ConnectFrames
        {
            init
            {
                foreach (var frame in value)
                {
                    EnqueueIncoming(frame);
                }
            }
        }

        public Func<string, Task<string?>>? OnSendAsync { get; init; }

        public List<string> SentTexts { get; } = [];

        public WebSocketState State { get; private set; } = WebSocketState.None;

        public Task ConnectAsync(Uri uri, string subprotocol, CancellationToken cancellationToken = default)
        {
            State = WebSocketState.Open;
            return Task.CompletedTask;
        }

        public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        {
            SentTexts.Add(text);
            if (OnSendAsync is not null)
            {
                var response = await OnSendAsync(text);
                if (response is not null)
                {
                    EnqueueIncoming(response);
                }
            }
        }

        public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken = default)
        {
            await _incomingSignal.WaitAsync(cancellationToken);
            var message = _incoming.Dequeue();
            if (message is null)
            {
                State = WebSocketState.Closed;
            }

            return message;
        }

        public Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken = default)
        {
            State = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = WebSocketState.Closed;
            return ValueTask.CompletedTask;
        }

        public void EnqueueIncoming(string? message)
        {
            _incoming.Enqueue(message);
            _incomingSignal.Release();
        }
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _handler(request);
    }
}
