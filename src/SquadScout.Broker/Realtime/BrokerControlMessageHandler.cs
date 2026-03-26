using System.Text.Json;
using Microsoft.Extensions.Logging;
using SquadScout.Broker.Projects;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Realtime;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Realtime;

public sealed class BrokerControlMessageHandler
{
    private readonly IProjectCatalog _projectCatalog;
    private readonly ISessionRelay _sessionRelay;
    private readonly ISessionOrchestrator _sessionOrchestrator;
    private readonly IRelayPublisher _relayPublisher;
    private readonly ILogger<BrokerControlMessageHandler> _logger;

    public BrokerControlMessageHandler(
        IProjectCatalog projectCatalog,
        ISessionRelay sessionRelay,
        ISessionOrchestrator sessionOrchestrator,
        IRelayPublisher relayPublisher,
        ILogger<BrokerControlMessageHandler> logger)
    {
        _projectCatalog = projectCatalog ?? throw new ArgumentNullException(nameof(projectCatalog));
        _sessionRelay = sessionRelay ?? throw new ArgumentNullException(nameof(sessionRelay));
        _sessionOrchestrator = sessionOrchestrator ?? throw new ArgumentNullException(nameof(sessionOrchestrator));
        _relayPublisher = relayPublisher ?? throw new ArgumentNullException(nameof(relayPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        MessageEnvelope<JsonElement>? envelope;
        try
        {
            envelope = payload.Deserialize<MessageEnvelope<JsonElement>>(SessionMessageSerializer.DefaultOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            _logger.LogWarning("Ignoring malformed broker control payload.");
            return;
        }

        if (envelope is null || !IsControlEnvelope(envelope))
        {
            _logger.LogDebug("Ignoring non-control broker payload.");
            return;
        }

        switch (envelope.MessageType)
        {
            case SessionMessageType.ProjectCatalogRequest:
                await HandleProjectCatalogAsync(envelope, cancellationToken).ConfigureAwait(false);
                break;
            case SessionMessageType.StartSessionRequest:
                await HandleStartSessionAsync(envelope, cancellationToken).ConfigureAwait(false);
                break;
            case SessionMessageType.SessionStatusRequest:
                await HandleSessionStatusAsync(envelope, cancellationToken).ConfigureAwait(false);
                break;
            default:
                _logger.LogDebug(
                    "Ignoring unsupported broker control message type {MessageType} for causation {MessageId}.",
                    envelope.MessageType,
                    envelope.MessageId);
                break;
        }
    }

    private async Task HandleProjectCatalogAsync(
        MessageEnvelope<JsonElement> envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            var _ = DeserializeRequired<ProjectCatalogRequestPayload>(envelope);
            var projects = (await _projectCatalog.ListAsync(cancellationToken).ConfigureAwait(false))
                .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await PublishResponseAsync(
                    envelope,
                    SessionMessageType.ProjectCatalogResponse,
                    new ProjectCatalogResponsePayload
                    {
                        Projects = projects,
                        Summary = projects.Length == 0
                            ? "The broker control channel is reachable, but no projects are registered yet."
                            : $"Loaded {projects.Length} project(s) from the broker control channel."
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Broker control project catalog request {MessageId} failed.", envelope.MessageId);
            await PublishResponseAsync(
                    envelope,
                    SessionMessageType.ProjectCatalogResponse,
                    new ProjectCatalogResponsePayload
                    {
                        Error = exception.Message,
                        Summary = "Project catalog retrieval failed."
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleStartSessionAsync(
        MessageEnvelope<JsonElement> envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = DeserializeRequired<StartSessionRequestPayload>(envelope);
            var session = await _sessionRelay.StartAsync(request.Command, cancellationToken).ConfigureAwait(false);

            await PublishResponseAsync(
                    envelope,
                    SessionMessageType.StartSessionResponse,
                    new StartSessionResponsePayload
                    {
                        Session = session,
                        Summary = "Started a broker-backed session through Azure Web PubSub."
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Broker control start session request {MessageId} failed.", envelope.MessageId);
            await PublishResponseAsync(
                    envelope,
                    SessionMessageType.StartSessionResponse,
                    new StartSessionResponsePayload
                    {
                        Error = exception.Message,
                        Summary = "Session start failed."
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleSessionStatusAsync(
        MessageEnvelope<JsonElement> envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = DeserializeRequired<SessionStatusRequestPayload>(envelope);
            var session = await _sessionOrchestrator.GetAsync(request.SessionId, cancellationToken).ConfigureAwait(false);

            await PublishResponseAsync(
                    envelope,
                    SessionMessageType.SessionStatusResponse,
                    new SessionStatusResponsePayload
                    {
                        Session = session
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Broker control session status request {MessageId} failed.", envelope.MessageId);
            await PublishResponseAsync(
                    envelope,
                    SessionMessageType.SessionStatusResponse,
                    new SessionStatusResponsePayload
                    {
                        Error = exception.Message
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task PublishResponseAsync<TPayload>(
        MessageEnvelope<JsonElement> requestEnvelope,
        SessionMessageType messageType,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        var response = new MessageEnvelope<TPayload>
        {
            ProjectId = BrokerControlChannel.ProjectId,
            SessionId = BrokerControlChannel.SessionId,
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = messageType,
            Direction = MessageDirection.BrokerToClient,
            TimestampUtc = DateTimeOffset.UtcNow,
            MessageId = $"broker-control-{Guid.NewGuid():N}",
            CorrelationId = string.IsNullOrWhiteSpace(requestEnvelope.CorrelationId)
                ? requestEnvelope.MessageId
                : requestEnvelope.CorrelationId,
            CausationId = requestEnvelope.MessageId,
            Payload = payload
        };

        await _relayPublisher.PublishEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static TPayload DeserializeRequired<TPayload>(MessageEnvelope<JsonElement> envelope)
    {
        var payload = envelope.Payload.Deserialize<TPayload>(SessionMessageSerializer.DefaultOptions);
        return payload ?? throw new InvalidOperationException("The broker control request payload is required.");
    }

    private static bool IsControlEnvelope(MessageEnvelope<JsonElement> envelope) =>
        envelope.Direction == MessageDirection.ClientToBroker &&
        string.Equals(envelope.ProjectId, BrokerControlChannel.ProjectId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(envelope.SessionId, BrokerControlChannel.SessionId, StringComparison.OrdinalIgnoreCase);
}
