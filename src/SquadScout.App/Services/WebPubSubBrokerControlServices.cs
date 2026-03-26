using System.Net.WebSockets;
using System.Text.Json;
using SquadScout.App.Configuration;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Realtime;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.Services;

public sealed class BrokerControlChannelClient
{
    private const string WebPubSubSubprotocol = "json.webpubsub.azure.v1";
    private readonly MessagingOptions _messagingOptions;
    private readonly IPubSubNegotiationClient _negotiationClient;
    private readonly IWebPubSubSocketFactory _socketFactory;
    private long _nextAckId;

    public BrokerControlChannelClient(
        MessagingOptions messagingOptions,
        IPubSubNegotiationClient negotiationClient,
        IWebPubSubSocketFactory socketFactory)
    {
        _messagingOptions = messagingOptions ?? throw new ArgumentNullException(nameof(messagingOptions));
        _negotiationClient = negotiationClient ?? throw new ArgumentNullException(nameof(negotiationClient));
        _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
    }

    public Task<ProjectCatalogResponsePayload> GetProjectCatalogAsync(
        string requestedBy,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync<ProjectCatalogRequestPayload, ProjectCatalogResponsePayload>(
            SessionMessageType.ProjectCatalogRequest,
            SessionMessageType.ProjectCatalogResponse,
            new ProjectCatalogRequestPayload
            {
                RequestedBy = requestedBy
            },
            cancellationToken);

    public Task<StartSessionResponsePayload> StartSessionAsync(
        StartSessionCommand command,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync<StartSessionRequestPayload, StartSessionResponsePayload>(
            SessionMessageType.StartSessionRequest,
            SessionMessageType.StartSessionResponse,
            new StartSessionRequestPayload
            {
                Command = command
            },
            cancellationToken);

    public Task<SessionStatusResponsePayload> GetSessionStatusAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync<SessionStatusRequestPayload, SessionStatusResponsePayload>(
            SessionMessageType.SessionStatusRequest,
            SessionMessageType.SessionStatusResponse,
            new SessionStatusRequestPayload
            {
                SessionId = sessionId
            },
            cancellationToken);

    private async Task<TResponsePayload> SendRequestAsync<TRequestPayload, TResponsePayload>(
        SessionMessageType requestType,
        SessionMessageType responseType,
        TRequestPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();

        var controlSession = BrokerControlChannel.CreateDescriptor();
        var negotiation = await _negotiationClient.NegotiateAsync(controlSession, cancellationToken).ConfigureAwait(false);
        var requestEnvelope = CreateRequestEnvelope(requestType, payload);
        var socket = _socketFactory.Create();

        await using (socket)
        {
            await socket.ConnectAsync(new Uri(negotiation.Url), WebPubSubSubprotocol, cancellationToken).ConfigureAwait(false);
            await WaitForConnectedAsync(socket, cancellationToken).ConfigureAwait(false);

            var ackId = Interlocked.Increment(ref _nextAckId);
            await socket.SendTextAsync(
                    JsonSerializer.Serialize(
                        new WebPubSubSendToGroupCommand
                        {
                            Group = negotiation.SessionGroup,
                            Data = JsonSerializer.SerializeToElement(requestEnvelope, SessionMessageSerializer.DefaultOptions),
                            AckId = ackId
                        },
                        SessionMessageSerializer.DefaultOptions),
                    cancellationToken)
                .ConfigureAwait(false);

            await WaitForAckAsync(socket, ackId, cancellationToken).ConfigureAwait(false);
            return await WaitForResponseAsync<TRequestPayload, TResponsePayload>(socket, requestEnvelope, responseType, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static MessageEnvelope<TPayload> CreateRequestEnvelope<TPayload>(
        SessionMessageType requestType,
        TPayload payload) =>
        new()
        {
            ProjectId = BrokerControlChannel.ProjectId,
            SessionId = BrokerControlChannel.SessionId,
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = requestType,
            Direction = MessageDirection.ClientToBroker,
            TimestampUtc = DateTimeOffset.UtcNow,
            MessageId = $"client-control-{Guid.NewGuid():N}",
            CorrelationId = $"mobile-control:{Guid.NewGuid():N}",
            Payload = payload
        };

    private static async Task WaitForConnectedAsync(IWebPubSubSocket socket, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var frame = await ReadFrameAsync(socket, cancellationToken).ConfigureAwait(false);
            if (frame.RootElement.TryGetProperty("type", out var typeProperty) &&
                string.Equals(typeProperty.GetString(), "system", StringComparison.OrdinalIgnoreCase) &&
                frame.RootElement.TryGetProperty("event", out var eventProperty) &&
                string.Equals(eventProperty.GetString(), "connected", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    private static async Task WaitForAckAsync(IWebPubSubSocket socket, long ackId, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var frame = await ReadFrameAsync(socket, cancellationToken).ConfigureAwait(false);
            if (!frame.RootElement.TryGetProperty("type", out var typeProperty) ||
                !string.Equals(typeProperty.GetString(), "ack", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (frame.RootElement.GetProperty("ackId").GetInt64() != ackId)
            {
                continue;
            }

            if (!frame.RootElement.GetProperty("success").GetBoolean())
            {
                throw new InvalidOperationException("Azure Web PubSub rejected the broker control request.");
            }

            return;
        }
    }

    private static async Task<TResponsePayload> WaitForResponseAsync<TRequestPayload, TResponsePayload>(
        IWebPubSubSocket socket,
        MessageEnvelope<TRequestPayload> requestEnvelope,
        SessionMessageType responseType,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var frame = await ReadFrameAsync(socket, cancellationToken).ConfigureAwait(false);
            if (!frame.RootElement.TryGetProperty("type", out var typeProperty) ||
                !string.Equals(typeProperty.GetString(), "message", StringComparison.OrdinalIgnoreCase) ||
                !frame.RootElement.TryGetProperty("data", out var dataProperty))
            {
                continue;
            }

            var envelope = dataProperty.Deserialize<MessageEnvelope<JsonElement>>(SessionMessageSerializer.DefaultOptions);
            if (envelope is null ||
                envelope.Direction != MessageDirection.BrokerToClient ||
                envelope.MessageType != responseType ||
                !string.Equals(envelope.CausationId, requestEnvelope.MessageId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = envelope.Payload.Deserialize<TResponsePayload>(SessionMessageSerializer.DefaultOptions);
            return payload ?? throw new InvalidOperationException("The broker control response payload was empty.");
        }
    }

    private static async Task<JsonDocument> ReadFrameAsync(IWebPubSubSocket socket, CancellationToken cancellationToken)
    {
        var text = await socket.ReceiveTextAsync(cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            throw new InvalidOperationException("The broker control channel disconnected before a response arrived.");
        }

        return JsonDocument.Parse(text);
    }

    private sealed record WebPubSubSendToGroupCommand
    {
        public string Type { get; init; } = "sendToGroup";

        public string Group { get; init; } = string.Empty;

        public string DataType { get; init; } = "json";

        public JsonElement Data { get; init; }

        public long AckId { get; init; }

        public bool NoEcho { get; init; } = true;
    }
}

public sealed class WebPubSubProjectCatalogService : IProjectCatalogService
{
    private readonly BrokerControlChannelClient _controlChannelClient;
    private readonly AppEnvironment _environment;
    private readonly LocalDevelopmentOptions _localDevelopmentOptions;

    public WebPubSubProjectCatalogService(
        BrokerControlChannelClient controlChannelClient,
        AppEnvironment environment,
        LocalDevelopmentOptions localDevelopmentOptions)
    {
        _controlChannelClient = controlChannelClient ?? throw new ArgumentNullException(nameof(controlChannelClient));
        _environment = environment;
        _localDevelopmentOptions = localDevelopmentOptions;
    }

    public async Task<ProjectCatalogSnapshot> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _controlChannelClient.GetProjectCatalogAsync("mobile-user", cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(response.Error))
            {
                throw new InvalidOperationException(response.Error);
            }

            var projects = response.Projects?.ToArray() ?? Array.Empty<RegisteredProject>();
            if (projects.Length > 0 || !CanUseFallbackProjects())
            {
                return new ProjectCatalogSnapshot(
                    projects,
                    ProjectCatalogSource.Broker,
                    string.IsNullOrWhiteSpace(response.Summary)
                        ? $"Loaded {projects.Length} project(s) from the broker control channel."
                        : response.Summary);
            }

            return CreateFallbackCatalog("The broker control channel returned no projects, so local development seeds are shown.");
        }
        catch (Exception ex) when (CanUseFallbackProjects() && ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return CreateFallbackCatalog("The broker control channel is unavailable, so local development seed projects are shown.");
        }
    }

    private bool CanUseFallbackProjects() =>
        _environment.IsDevelopment &&
        _localDevelopmentOptions.UseSampleProjectsWhenBrokerUnavailable &&
        _localDevelopmentOptions.SeedProjects.Count > 0;

    private ProjectCatalogSnapshot CreateFallbackCatalog(string summary)
    {
        var projects = _localDevelopmentOptions.SeedProjects
            .Select(project => project.ToRegisteredProject())
            .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ProjectCatalogSnapshot(projects, ProjectCatalogSource.DevelopmentFallback, summary);
    }
}

public sealed class WebPubSubSessionLifecycleService : ISessionLifecycleService
{
    internal const string LocalDevelopmentSessionPrefix = "localdev-";

    private readonly BrokerControlChannelClient _controlChannelClient;
    private readonly AppEnvironment _environment;
    private readonly LocalDevelopmentOptions _localDevelopmentOptions;

    public WebPubSubSessionLifecycleService(
        BrokerControlChannelClient controlChannelClient,
        AppEnvironment environment,
        LocalDevelopmentOptions localDevelopmentOptions)
    {
        _controlChannelClient = controlChannelClient ?? throw new ArgumentNullException(nameof(controlChannelClient));
        _environment = environment;
        _localDevelopmentOptions = localDevelopmentOptions;
    }

    public async Task<SessionLaunchResult> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _controlChannelClient.StartSessionAsync(command, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(response.Error))
            {
                throw new InvalidOperationException(response.Error);
            }

            var session = response.Session
                ?? throw new InvalidOperationException("The broker control channel did not return a session descriptor.");

            return new SessionLaunchResult(
                session,
                SessionActivationSource.Broker,
                string.IsNullOrWhiteSpace(response.Summary)
                    ? "Started a broker-backed session through Azure Web PubSub."
                    : response.Summary);
        }
        catch (Exception ex) when (CanCreateOfflineSession() && ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            var session = new SessionDescriptor
            {
                SessionId = $"{LocalDevelopmentSessionPrefix}{Guid.NewGuid():N}",
                ProjectId = command.ProjectId,
                State = SessionState.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            return new SessionLaunchResult(
                session,
                SessionActivationSource.DevelopmentFallback,
                "Created a local-development pending session scaffold because the broker control channel is unavailable.");
        }
    }

    public async Task<SessionDescriptor?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            sessionId.StartsWith(LocalDevelopmentSessionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var response = await _controlChannelClient.GetSessionStatusAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            throw new InvalidOperationException(response.Error);
        }

        return response.Session;
    }

    private bool CanCreateOfflineSession() =>
        _environment.IsDevelopment &&
        _localDevelopmentOptions.CreateOfflineSessionsWhenBrokerUnavailable;
}
