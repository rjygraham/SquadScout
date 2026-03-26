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
        await WaitForAsync(() => service.RecentTraffic.Count >= 5);

        Assert.Equal(MessageConnectionState.Connected, service.CurrentStatus.State);
        Assert.DoesNotContain("gap", service.CurrentStatus.Summary, StringComparison.OrdinalIgnoreCase);

        await service.SendInputAsync("after-recovery");
        await WaitForAsync(() => service.RecentTraffic.Count >= 6);

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
    public async Task PrepareForSessionAsyncSchedulesTokenRefreshBeforeRefreshAtUtc()
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

        Assert.Equal(TimeSpan.FromMinutes(45), Assert.Single(delay.RequestedDelays));

        delay.ReleaseNext();

        await WaitForAsync(() =>
            negotiationClient.CallCount == 2 &&
            service.CurrentStatus.State == MessageConnectionState.Connected &&
            service.CurrentStatus.ConnectionId == "conn-refresh-2");

        await WaitForAsync(() => delay.RequestedDelays.Count >= 2);

        Assert.Equal(TimeSpan.FromMinutes(90), delay.RequestedDelays[1]);
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
    public async Task TokenRefreshFailureTransitionsToFaultedWithActionableGuidance()
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

        var negotiationClient = new ScriptedNegotiationClient(
            CreateNegotiationResponse(session, now.AddMinutes(10)),
            new InvalidOperationException("Negotiate failed with 401 (Unauthorized). The negotiate endpoint requires a trusted identity."));

        await using var service = CreateService(
            negotiationClient,
            () => now,
            delay.DelayAsync,
            firstSocket);

        await service.PrepareForSessionAsync(session);

        await WaitForAsync(() => delay.RequestedDelays.Count == 1);
        delay.ReleaseNext();

        await WaitForAsync(() => service.CurrentStatus.State == MessageConnectionState.Faulted);

        Assert.Contains("Token refresh failed", service.CurrentStatus.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Token refresh failed", service.CurrentStatus.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retry the live transport", service.CurrentStatus.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trusted identity", service.CurrentStatus.FailureReason, StringComparison.OrdinalIgnoreCase);
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
        DateTimeOffset? refreshAtUtc = null) =>
        new()
        {
            Url = "wss://example.webpubsub.azure.com/client/hubs/squadscout?access_token=test",
            Hub = "squadscout",
            UserId = $"client:{session.ProjectId}:{session.SessionId}:mobile-user",
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            ParticipantKind = PubSubParticipantKind.Client,
            SessionGroup = $"session:{session.ProjectId}:{session.SessionId}",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
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

    private static MessageEnvelope<ReplayResponsePayload> CreateReplayResponseEnvelope(
        SessionDescriptor session,
        long generation = SessionEnvelopeContract.InitialGeneration,
        bool gapDetected = false,
        long? availableFromSequence = null,
        long? availableToSequence = null,
        IReadOnlyList<MessageEnvelope<JsonElement>>? messages = null,
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
            MessageId = $"replay-{Guid.NewGuid():n}",
            CorrelationId = correlationId ?? $"broker-session:{session.SessionId}",
            CausationId = causationId,
            Payload = new ReplayResponsePayload
            {
                Generation = generation,
                AvailableFromSequence = availableFromSequence,
                AvailableToSequence = availableToSequence,
                GapDetected = gapDetected,
                IsComplete = true,
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
