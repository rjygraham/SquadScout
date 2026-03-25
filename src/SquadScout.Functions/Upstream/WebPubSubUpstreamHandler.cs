using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Realtime;

namespace SquadScout.Functions.Upstream;

public sealed class WebPubSubUpstreamHandler
{
    public const string WebHookRequestOriginHeaderName = "WebHook-Request-Origin";
    public const string WebHookAllowedOriginHeaderName = "WebHook-Allowed-Origin";
    public const string CloudEventTypeHeaderName = "ce-type";
    public const string CloudEventConnectionIdHeaderName = "ce-connectionId";

    private static readonly string InputCloudEventType = $"azure.webpubsub.user.{SessionUpstreamEventNames.Input}";
    private readonly WebPubSubUpstreamAuthenticator _authenticator;
    private readonly BrokerInputForwarder _brokerInputForwarder;
    private readonly ILogger<WebPubSubUpstreamHandler> _logger;

    public WebPubSubUpstreamHandler(
        WebPubSubUpstreamAuthenticator authenticator,
        BrokerInputForwarder brokerInputForwarder,
        ILogger<WebPubSubUpstreamHandler> logger)
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _brokerInputForwarder = brokerInputForwarder ?? throw new ArgumentNullException(nameof(brokerInputForwarder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WebPubSubUpstreamResponse> HandleAsync(
        string method,
        HttpHeadersCollection headers,
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

        if (!string.Equals(eventType, InputCloudEventType, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Ignoring unsupported Azure Web PubSub upstream event type {EventType}.", eventType);
            return new WebPubSubUpstreamResponse(HttpStatusCode.NoContent, allowedOrigin);
        }

        MessageEnvelope<InputChunkPayload>? envelope;
        try
        {
            envelope = await JsonSerializer.DeserializeAsync<MessageEnvelope<InputChunkPayload>>(
                    body,
                    SessionMessageSerializer.DefaultOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request body must be a valid JSON input envelope.");
        }

        if (envelope is null || envelope.Payload is null)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request body is required.");
        }

        if (envelope.MessageType != SessionMessageType.Input)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request must contain an input envelope.");
        }

        if (envelope.Direction != MessageDirection.ClientToBroker)
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request must contain a client-to-broker envelope.");
        }

        if (string.IsNullOrWhiteSpace(envelope.ProjectId) || string.IsNullOrWhiteSpace(envelope.SessionId))
        {
            return Error(HttpStatusCode.BadRequest, allowedOrigin, "The upstream request must include projectId and sessionId.");
        }

        if (!TryResolvePhaseOneSessionGroup(envelope, out var sessionGroup, out var sessionGroupError))
        {
            _logger.LogWarning(
                "Rejected Azure Web PubSub input {MessageId} for project {ProjectId} session {SessionId} from connection {ConnectionId}: {FailureMessage}",
                envelope.MessageId,
                envelope.ProjectId,
                envelope.SessionId,
                connectionId ?? "<missing>",
                sessionGroupError);
            return Error(HttpStatusCode.BadRequest, allowedOrigin, sessionGroupError);
        }

        var brokerResponse = await _brokerInputForwarder.ForwardAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (brokerResponse.IsSuccessStatusCode)
        {
            _logger.LogDebug(
                "Forwarded Azure Web PubSub input {MessageId} for project {ProjectId} session {SessionId} through session group {SessionGroup} from connection {ConnectionId} to the broker.",
                envelope.MessageId,
                envelope.ProjectId,
                envelope.SessionId,
                sessionGroup,
                connectionId ?? "<missing>");

            return new WebPubSubUpstreamResponse(HttpStatusCode.NoContent, allowedOrigin);
        }

        _logger.LogWarning(
            "Broker rejected Azure Web PubSub input {MessageId} for project {ProjectId} session {SessionId} through session group {SessionGroup} from connection {ConnectionId} with status code {StatusCode}. Broker response: {BrokerResponseBody}",
            envelope.MessageId,
            envelope.ProjectId,
            envelope.SessionId,
            sessionGroup,
            connectionId ?? "<missing>",
            brokerResponse.StatusCode,
            brokerResponse.Body ?? "<empty>");

        return new WebPubSubUpstreamResponse(
            brokerResponse.StatusCode,
            allowedOrigin,
            brokerResponse.Body ?? SerializeBody(new { error = "Broker input forwarding failed." }),
            "application/json");
    }

    private static bool TryResolvePhaseOneSessionGroup(
        MessageEnvelope<InputChunkPayload> envelope,
        out string sessionGroup,
        out string validationError)
    {
        if (SessionGroupName.TryCreate(envelope.ProjectId, envelope.SessionId, brokerId: null, out sessionGroup, out validationError))
        {
            return true;
        }

        validationError =
            $"The upstream request projectId/sessionId must map to a valid Phase 1 session group. {validationError}";
        return false;
    }

    private static WebPubSubUpstreamResponse Error(HttpStatusCode statusCode, string? allowedOrigin, string error) =>
        new(
            statusCode,
            allowedOrigin,
            SerializeBody(new { error }),
            "application/json");

    private static string? GetHeaderValue(HttpHeadersCollection headers, string headerName) =>
        headers.TryGetValues(headerName, out var values)
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
