using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Realtime;

namespace SquadScout.Broker.Realtime;

public sealed class WebPubSubUpstreamHandler
{
    public const string WebHookRequestOriginHeaderName = "WebHook-Request-Origin";
    public const string WebHookAllowedOriginHeaderName = "WebHook-Allowed-Origin";
    public const string CloudEventTypeHeaderName = "ce-type";
    public const string CloudEventConnectionIdHeaderName = "ce-connectionId";

    private static readonly string HeartbeatCloudEventType = $"azure.webpubsub.user.{SessionUpstreamEventNames.Heartbeat}";
    private static readonly string InputCloudEventType = $"azure.webpubsub.user.{SessionUpstreamEventNames.Input}";
    private static readonly string ReplayCloudEventType = $"azure.webpubsub.user.{SessionUpstreamEventNames.ReplayRequest}";

    private readonly WebPubSubUpstreamAuthenticator _authenticator;
    private readonly ISessionRelay _sessionRelay;
    private readonly ISessionOrchestrator _orchestrator;
    private readonly ISessionLivenessManager _livenessManager;
    private readonly IRelayPublisher _relayPublisher;
    private readonly ILogger<WebPubSubUpstreamHandler> _logger;

    public WebPubSubUpstreamHandler(
        WebPubSubUpstreamAuthenticator authenticator,
        ISessionRelay sessionRelay,
        ISessionOrchestrator orchestrator,
        ISessionLivenessManager livenessManager,
        IRelayPublisher relayPublisher,
        ILogger<WebPubSubUpstreamHandler> logger)
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _sessionRelay = sessionRelay ?? throw new ArgumentNullException(nameof(sessionRelay));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _livenessManager = livenessManager ?? throw new ArgumentNullException(nameof(livenessManager));
        _relayPublisher = relayPublisher ?? throw new ArgumentNullException(nameof(relayPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WebPubSubUpstreamResponse> HandleAsync(
        string method,
        IHeaderDictionary headers,
        Stream body,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(body);
        cancellationToken.ThrowIfCancellationRequested();

        var allowedOrigin = GetHeaderValue(headers, WebHookRequestOriginHeaderName);
        var connectionId = GetHeaderValue(headers, CloudEventConnectionIdHeaderName);
        if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            return new WebPubSubUpstreamResponse(HttpStatusCode.OK, allowedOrigin);
        }

        if (!_authenticator.TryAuthenticate(headers, out var authenticationFailure))
        {
            _logger.LogWarning(
                "Rejected Azure Web PubSub upstream request for connection {ConnectionId} from origin {Origin}: {FailureMessage}",
                connectionId ?? "<missing>",
                allowedOrigin ?? "<missing>",
                authenticationFailure);
            return Error(HttpStatusCode.Unauthorized, allowedOrigin, authenticationFailure);
        }

        var eventType = GetHeaderValue(headers, CloudEventTypeHeaderName);
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "Azure Web PubSub upstream requests must include the ce-type header.");
        }

        if (!IsSupportedEventType(eventType))
        {
            _logger.LogDebug("Ignoring unsupported Azure Web PubSub upstream event type {EventType}.", eventType);
            return new WebPubSubUpstreamResponse(HttpStatusCode.NoContent, allowedOrigin);
        }

        string jsonBody;
        try
        {
            jsonBody = await ReadBodyAsStringAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        MessageEnvelope<JsonElement>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(jsonBody, SessionMessageSerializer.DefaultOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request body must be a valid JSON session envelope.");
        }

        if (envelope is null || envelope.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request body is required.");
        }

        if (envelope.Direction != MessageDirection.ClientToBroker)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request must contain a client-to-broker envelope.");
        }

        if (string.IsNullOrWhiteSpace(envelope.ProjectId) || string.IsNullOrWhiteSpace(envelope.SessionId))
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request must include projectId and sessionId.");
        }

        if (!TryResolvePhaseOneSessionGroup(envelope.ProjectId, envelope.SessionId, out var sessionGroup, out var sessionGroupError))
        {
            _logger.LogWarning(
                "Rejected Azure Web PubSub {MessageType} {MessageId} for project {ProjectId} session {SessionId} from connection {ConnectionId}: {FailureMessage}",
                envelope.MessageType,
                envelope.MessageId,
                envelope.ProjectId,
                envelope.SessionId,
                connectionId ?? "<missing>",
                sessionGroupError);
            return Error(HttpStatusCode.BadRequest, allowedOrigin, sessionGroupError);
        }

        return envelope.MessageType switch
        {
            SessionMessageType.Heartbeat => await HandleHeartbeatAsync(jsonBody, sessionGroup, connectionId, allowedOrigin, cancellationToken).ConfigureAwait(false),
            SessionMessageType.Input => await HandleInputAsync(jsonBody, sessionGroup, connectionId, allowedOrigin, cancellationToken).ConfigureAwait(false),
            SessionMessageType.ReplayRequest => await HandleReplayAsync(jsonBody, sessionGroup, connectionId, allowedOrigin, cancellationToken).ConfigureAwait(false),
            _ => Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request must contain a heartbeat, input, or replay-request envelope.")
        };
    }

    private async Task<WebPubSubUpstreamResponse> HandleHeartbeatAsync(
        string jsonBody,
        string sessionGroup,
        string? connectionId,
        string? allowedOrigin,
        CancellationToken cancellationToken)
    {
        MessageEnvelope<HeartbeatPayload>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<MessageEnvelope<HeartbeatPayload>>(jsonBody, SessionMessageSerializer.DefaultOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request body must be a valid JSON heartbeat envelope.");
        }

        if (envelope?.Payload is null)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request body is required.");
        }

        if (envelope.MessageType != SessionMessageType.Heartbeat)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request must contain a heartbeat envelope.");
        }

        if (!_livenessManager.CanAcceptHeartbeat(envelope.SessionId, envelope.Payload, out var validationError))
        {
            _logger.LogWarning(
                "Rejected Azure Web PubSub heartbeat {MessageId} for project {ProjectId} session {SessionId} through session group {SessionGroup} from connection {ConnectionId}: {FailureMessage}",
                envelope.MessageId,
                envelope.ProjectId,
                envelope.SessionId,
                sessionGroup,
                connectionId ?? "<missing>",
                validationError);

            return Error(HttpStatusCode.BadRequest, allowedOrigin, validationError);
        }

        try
        {
            var validation = await _orchestrator.ValidateClientMessageAsync(envelope.SessionId, envelope, cancellationToken).ConfigureAwait(false);
            if (validation.IsAccepted && validation.Status != SequenceValidationStatus.Duplicate)
            {
                if (!_livenessManager.TryCommitHeartbeat(envelope.SessionId, envelope.Payload))
                {
                    return Error(HttpStatusCode.BadRequest, allowedOrigin, "Heartbeat acknowledgement could not be applied because the nonce was no longer valid.");
                }
            }

            return CreateValidationResponse(validation, allowedOrigin);
        }
        catch (ArgumentException ex)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return JsonResponse(HttpStatusCode.NotFound, allowedOrigin, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return JsonResponse(HttpStatusCode.Conflict, allowedOrigin, new { message = ex.Message });
        }
    }

    private async Task<WebPubSubUpstreamResponse> HandleInputAsync(
        string jsonBody,
        string sessionGroup,
        string? connectionId,
        string? allowedOrigin,
        CancellationToken cancellationToken)
    {
        MessageEnvelope<InputChunkPayload>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<MessageEnvelope<InputChunkPayload>>(jsonBody, SessionMessageSerializer.DefaultOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request body must be a valid JSON input envelope.");
        }

        if (envelope?.Payload is null)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request body is required.");
        }

        if (envelope.MessageType != SessionMessageType.Input)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request must contain an input envelope.");
        }

        try
        {
            var validation = await _sessionRelay.RelayInputAsync(envelope.SessionId, envelope, cancellationToken).ConfigureAwait(false);
            if (validation.IsAccepted)
            {
                _logger.LogDebug(
                    "Accepted Azure Web PubSub input {MessageId} for project {ProjectId} session {SessionId} through session group {SessionGroup} from connection {ConnectionId}.",
                    envelope.MessageId,
                    envelope.ProjectId,
                    envelope.SessionId,
                    sessionGroup,
                    connectionId ?? "<missing>");
            }
            else
            {
                _logger.LogWarning(
                    "Rejected Azure Web PubSub input {MessageId} for project {ProjectId} session {SessionId} through session group {SessionGroup} from connection {ConnectionId} with validation status {ValidationStatus}.",
                    envelope.MessageId,
                    envelope.ProjectId,
                    envelope.SessionId,
                    sessionGroup,
                    connectionId ?? "<missing>",
                    validation.Status);
            }

            return CreateValidationResponse(validation, allowedOrigin);
        }
        catch (ArgumentException ex)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, ex.Message);
        }
        catch (SessionControlException ex)
        {
            return JsonResponse((HttpStatusCode)ex.StatusCode, allowedOrigin, new
            {
                code = ex.Code,
                message = ex.Message,
                sessionId = ex.SessionId,
                projectId = ex.ProjectId,
                state = ex.SessionState?.ToString()
            });
        }
        catch (KeyNotFoundException ex)
        {
            return JsonResponse(HttpStatusCode.NotFound, allowedOrigin, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return JsonResponse(HttpStatusCode.Conflict, allowedOrigin, new { message = ex.Message });
        }
    }

    private async Task<WebPubSubUpstreamResponse> HandleReplayAsync(
        string jsonBody,
        string sessionGroup,
        string? connectionId,
        string? allowedOrigin,
        CancellationToken cancellationToken)
    {
        MessageEnvelope<ReplayRequestPayload>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<MessageEnvelope<ReplayRequestPayload>>(jsonBody, SessionMessageSerializer.DefaultOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request body must be a valid JSON replay-request envelope.");
        }

        if (envelope?.Payload is null)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request body is required.");
        }

        if (envelope.MessageType != SessionMessageType.ReplayRequest)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request must contain a replay-request envelope.");
        }

        try
        {
            var replayResponse = await _orchestrator.ReplayAsync(envelope.SessionId, envelope, cancellationToken).ConfigureAwait(false);
            await _relayPublisher.PublishEnvelopeAsync(replayResponse, cancellationToken).ConfigureAwait(false);
            _livenessManager.RecordClientActivity(envelope.SessionId);

            _logger.LogDebug(
                "Published replay response {ReplayMessageId} for replay request {ReplayRequestMessageId} for project {ProjectId} session {SessionId} through session group {SessionGroup} from connection {ConnectionId}.",
                replayResponse.MessageId,
                envelope.MessageId,
                envelope.ProjectId,
                envelope.SessionId,
                sessionGroup,
                connectionId ?? "<missing>");

            return new WebPubSubUpstreamResponse(HttpStatusCode.NoContent, allowedOrigin);
        }
        catch (ArgumentException ex)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, ex.Message);
        }
        catch (SessionControlException ex)
        {
            return JsonResponse((HttpStatusCode)ex.StatusCode, allowedOrigin, new
            {
                code = ex.Code,
                message = ex.Message,
                sessionId = ex.SessionId,
                projectId = ex.ProjectId,
                state = ex.SessionState?.ToString()
            });
        }
        catch (KeyNotFoundException ex)
        {
            return JsonResponse(HttpStatusCode.NotFound, allowedOrigin, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return JsonResponse(HttpStatusCode.Conflict, allowedOrigin, new { message = ex.Message });
        }
    }

    private static WebPubSubUpstreamResponse CreateValidationResponse(SequenceValidationResult validation, string? allowedOrigin) =>
        validation.Status switch
        {
            SequenceValidationStatus.Accepted or SequenceValidationStatus.Duplicate or SequenceValidationStatus.GapDetected
                => new(HttpStatusCode.NoContent, allowedOrigin),
            SequenceValidationStatus.StaleGeneration or SequenceValidationStatus.FutureGeneration
                => JsonResponse(HttpStatusCode.Conflict, allowedOrigin, validation),
            SequenceValidationStatus.InvalidEnvelope
                => JsonResponse(HttpStatusCode.BadRequest, allowedOrigin, validation),
            _ => JsonResponse(HttpStatusCode.BadRequest, allowedOrigin, validation)
        };

    private static bool IsSupportedEventType(string eventType) =>
        string.Equals(eventType, HeartbeatCloudEventType, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(eventType, InputCloudEventType, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(eventType, ReplayCloudEventType, StringComparison.OrdinalIgnoreCase);

    private static bool TryResolvePhaseOneSessionGroup(
        string projectId,
        string sessionId,
        out string sessionGroup,
        out string validationError)
    {
        if (SessionGroupName.TryCreate(projectId, sessionId, brokerId: null, out sessionGroup, out validationError))
        {
            return true;
        }

        validationError =
            $"The upstream request projectId/sessionId must map to a valid Phase 1 session group. {validationError}";
        return false;
    }

    private static async Task<string> ReadBodyAsStringAsync(Stream body, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static WebPubSubUpstreamResponse Error(HttpStatusCode statusCode, string? allowedOrigin, string error) =>
        JsonResponse(statusCode, allowedOrigin, new { error });

    private static WebPubSubUpstreamResponse JsonResponse<TPayload>(HttpStatusCode statusCode, string? allowedOrigin, TPayload payload) =>
        new(
            statusCode,
            allowedOrigin,
            SerializeBody(payload),
            "application/json");

    private static string? GetHeaderValue(IHeaderDictionary headers, string headerName) =>
        headers.TryGetValue(headerName, out var values)
            ? values.FirstOrDefault()
            : null;

    private static string SerializeBody<TPayload>(TPayload payload) =>
        JsonSerializer.Serialize(payload, SessionMessageSerializer.DefaultOptions);
}

public sealed record WebPubSubUpstreamResponse(
    HttpStatusCode StatusCode,
    string? AllowedOrigin,
    string? Body = null,
    string? ContentType = null);
