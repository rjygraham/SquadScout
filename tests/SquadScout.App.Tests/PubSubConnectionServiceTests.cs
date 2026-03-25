using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using SquadScout.App.Configuration;
using SquadScout.App.Services;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Realtime;
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

        firstSocket.EnqueueIncoming(SystemMessage("disconnected"));
        await WaitForAsync(() => service.CurrentStatus.State == MessageConnectionState.Faulted);

        var status = await service.ReconnectAsync();

        Assert.Equal(MessageConnectionState.Connected, status.State);
        Assert.Equal("conn-second", status.ConnectionId);
        Assert.Equal(1, status.ReconnectAttempt);
        Assert.Equal(2, negotiationClient.CallCount);
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

    private static SessionDescriptor CreateSession() =>
        new()
        {
            ProjectId = "proj-01",
            SessionId = "session-abc",
            State = SessionState.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

    private static PubSubNegotiateResponse CreateNegotiationResponse(SessionDescriptor session) =>
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
            RefreshAtUtc = DateTimeOffset.UtcNow.AddMinutes(50)
        };

    private static MessageEnvelope<OutputChunkPayload> CreateBrokerEnvelope(SessionDescriptor session, long sequence) =>
        new()
        {
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.Output,
            Direction = MessageDirection.BrokerToClient,
            Sequence = sequence,
            TimestampUtc = DateTimeOffset.UtcNow,
            MessageId = $"broker-{sequence}",
            CorrelationId = $"broker-session:{session.SessionId}",
            Payload = new OutputChunkPayload
            {
                Content = "ready",
                IsError = false
            }
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
        new(
            new MessagingOptions
            {
                Hub = "squadscout",
                NegotiateUrl = "http://127.0.0.1:7071/api/negotiate",
                ConnectTimeoutSeconds = 5,
                CommandAckTimeoutSeconds = 5,
                RecentTrafficCapacity = 20
            },
            negotiationClient,
            new FakeWebPubSubSocketFactory(sockets));

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
