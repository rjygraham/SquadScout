using System.Net.WebSockets;
using System.Text.Json;
using SquadScout.App.Configuration;
using SquadScout.App.Services;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Realtime;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.Tests;

public sealed class BrokerControlChannelClientTests
{
    [Fact]
    public async Task GetProjectCatalogAsyncUsesSendToGroupAndReturnsBrokerResponse()
    {
        var socket = new FakeWebPubSubSocket
        {
            ConnectFrames =
            [
                SystemMessage("connected", "conn-control")
            ],
            OnSendAsync = command =>
            {
                using var json = JsonDocument.Parse(command);
                var root = json.RootElement;
                var ackId = root.GetProperty("ackId").GetInt64();
                var requestEnvelope = root.GetProperty("data")
                    .Deserialize<MessageEnvelope<JsonElement>>(SessionMessageSerializer.DefaultOptions)!;

                var responseEnvelope = new MessageEnvelope<ProjectCatalogResponsePayload>
                {
                    ProjectId = BrokerControlChannel.ProjectId,
                    SessionId = BrokerControlChannel.SessionId,
                    Generation = SessionEnvelopeContract.InitialGeneration,
                    MessageType = SessionMessageType.ProjectCatalogResponse,
                    Direction = MessageDirection.BrokerToClient,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    MessageId = "response-1",
                    CorrelationId = requestEnvelope.CorrelationId,
                    CausationId = requestEnvelope.MessageId,
                    Payload = new ProjectCatalogResponsePayload
                    {
                        Projects =
                        [
                            new RegisteredProject
                            {
                                ProjectId = "proj-01",
                                DisplayName = "Project One",
                                RepositoryRoot = @"D:\GitHub\ProjectOne"
                            }
                        ],
                        Summary = "Loaded 1 project(s) from the broker control channel."
                    }
                };

                return Task.FromResult<string?>(AckAndGroupMessage(ackId, responseEnvelope));
            }
        };

        var client = CreateControlClient(socket);

        var response = await client.GetProjectCatalogAsync("tests");

        Assert.Single(response.Projects);
        Assert.Equal("proj-01", response.Projects[0].ProjectId);
        Assert.Single(socket.SentTexts);

        using var command = JsonDocument.Parse(socket.SentTexts[0]);
        Assert.Equal("sendToGroup", command.RootElement.GetProperty("type").GetString());
        Assert.Equal("session:broker-control:phase1", command.RootElement.GetProperty("group").GetString());

        var requestEnvelope = command.RootElement.GetProperty("data")
            .Deserialize<MessageEnvelope<JsonElement>>(SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(requestEnvelope);
        Assert.Equal(SessionMessageType.ProjectCatalogRequest, requestEnvelope!.MessageType);
        Assert.Equal(MessageDirection.ClientToBroker, requestEnvelope.Direction);
    }

    [Fact]
    public async Task ProjectCatalogFallsBackToDevelopmentSeedsWhenControlChannelIsUnavailable()
    {
        var service = new WebPubSubProjectCatalogService(
            CreateThrowingControlClient(),
            new AppEnvironment(AppEnvironment.DevelopmentName),
            new LocalDevelopmentOptions
            {
                UseSampleProjectsWhenBrokerUnavailable = true,
                SeedProjects =
                [
                    new SeedProjectOptions
                    {
                        ProjectId = "squadscout",
                        DisplayName = "SquadScout",
                        RepositoryRoot = @"D:\GitHub\SquadScout-69"
                    }
                ]
            });

        var snapshot = await service.GetProjectsAsync();

        Assert.Equal(ProjectCatalogSource.DevelopmentFallback, snapshot.Source);
        Assert.Single(snapshot.Projects);
    }

    [Fact]
    public async Task SessionLifecycleCreatesDevelopmentPendingSessionWhenControlChannelIsUnavailable()
    {
        var service = new WebPubSubSessionLifecycleService(
            CreateThrowingControlClient(),
            new AppEnvironment(AppEnvironment.DevelopmentName),
            new LocalDevelopmentOptions
            {
                CreateOfflineSessionsWhenBrokerUnavailable = true
            });

        var result = await service.StartAsync(new StartSessionCommand
        {
            ProjectId = "squadscout",
            RequestedBy = "tests"
        });

        Assert.Equal(SessionActivationSource.DevelopmentFallback, result.Source);
        Assert.StartsWith("localdev-", result.Session.SessionId, StringComparison.OrdinalIgnoreCase);
    }

    private static BrokerControlChannelClient CreateControlClient(FakeWebPubSubSocket socket) =>
        new(
            new MessagingOptions
            {
                Hub = "squadscout",
                NegotiateUrl = "http://127.0.0.1:7071/api/negotiate",
                ConnectTimeoutSeconds = 5,
                CommandAckTimeoutSeconds = 5
            },
            new RecordingNegotiationClient(),
            new FakeWebPubSubSocketFactory(socket));

    private static BrokerControlChannelClient CreateThrowingControlClient() =>
        new(
            new MessagingOptions
            {
                Hub = "squadscout",
                NegotiateUrl = "http://127.0.0.1:7071/api/negotiate",
                ConnectTimeoutSeconds = 5,
                CommandAckTimeoutSeconds = 5
            },
            new ThrowingNegotiationClient(),
            new FakeWebPubSubSocketFactory(new FakeWebPubSubSocket()));

    private static string SystemMessage(string eventName, string connectionId) =>
        JsonSerializer.Serialize(
            new
            {
                type = "system",
                @event = eventName,
                connectionId
            },
            SessionMessageSerializer.DefaultOptions);

    private static string AckAndGroupMessage<TPayload>(long ackId, MessageEnvelope<TPayload> responseEnvelope) =>
        JsonSerializer.Serialize(
            new object[]
            {
                new
                {
                    type = "ack",
                    ackId,
                    success = true
                },
                new
                {
                    type = "message",
                    from = "group",
                    group = $"session:{responseEnvelope.ProjectId}:{responseEnvelope.SessionId}",
                    dataType = "json",
                    data = responseEnvelope
                }
            },
            SessionMessageSerializer.DefaultOptions);

    private sealed class RecordingNegotiationClient : IPubSubNegotiationClient
    {
        public Task<PubSubNegotiateResponse> NegotiateAsync(SessionDescriptor session, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PubSubNegotiateResponse
            {
                Url = "wss://example.invalid/client/hubs/squadscout",
                Hub = "squadscout",
                UserId = "client:broker-control:phase1",
                ProjectId = BrokerControlChannel.ProjectId,
                SessionId = BrokerControlChannel.SessionId,
                ParticipantKind = PubSubParticipantKind.Client,
                SessionGroup = "session:broker-control:phase1",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30),
                RefreshAtUtc = DateTimeOffset.UtcNow.AddMinutes(25)
            });
    }

    private sealed class ThrowingNegotiationClient : IPubSubNegotiationClient
    {
        public Task<PubSubNegotiateResponse> NegotiateAsync(SessionDescriptor session, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The broker control channel is unavailable.");
    }

    private sealed class FakeWebPubSubSocketFactory(FakeWebPubSubSocket socket) : IWebPubSubSocketFactory
    {
        public IWebPubSubSocket Create() => socket;
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
            if (OnSendAsync is null)
            {
                throw new InvalidOperationException("The broker control test socket was not configured with a send handler.");
            }

            var response = await OnSendAsync(text);
            if (response is not null)
            {
                try
                {
                    using var document = JsonDocument.Parse(response);
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in document.RootElement.EnumerateArray())
                        {
                            EnqueueIncoming(item.GetRawText());
                        }
                    }
                    else
                    {
                        EnqueueIncoming(response);
                    }
                }
                catch (JsonException)
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

        private void EnqueueIncoming(string? frame)
        {
            _incoming.Enqueue(frame);
            _incomingSignal.Release();
        }
    }
}
