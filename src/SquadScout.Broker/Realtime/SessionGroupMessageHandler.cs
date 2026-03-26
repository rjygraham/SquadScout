using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Realtime;

public sealed class SessionGroupMessageHandler
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISessionGroupResolver _sessionGroupResolver;
    private readonly ILogger<SessionGroupMessageHandler> _logger;

    public SessionGroupMessageHandler(
        IServiceProvider serviceProvider,
        ISessionGroupResolver sessionGroupResolver,
        ILogger<SessionGroupMessageHandler> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _sessionGroupResolver = sessionGroupResolver ?? throw new ArgumentNullException(nameof(sessionGroupResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task HandleAsync(string sessionGroup, JsonElement data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sessionGroup))
        {
            throw new ArgumentException("A session group is required.", nameof(sessionGroup));
        }

        var envelope = data.Deserialize<MessageEnvelope<JsonElement>>(SessionMessageSerializer.DefaultOptions)
            ?? throw new InvalidOperationException("Azure Web PubSub delivered a malformed session envelope.");

        if (envelope.Direction != MessageDirection.ClientToBroker)
        {
            _logger.LogDebug(
                "Ignoring broker-authored echo {MessageType} for session group {SessionGroup}.",
                envelope.MessageType,
                sessionGroup);
            return;
        }

        if (!TryValidateSessionGroup(sessionGroup, envelope))
        {
            return;
        }

        switch (envelope.MessageType)
        {
            case SessionMessageType.Input:
            {
                var inputEnvelope = DeserializeEnvelope<InputChunkPayload>(data, "input");
                await _serviceProvider.GetRequiredService<ISessionRelay>()
                    .RelayInputAsync(inputEnvelope.SessionId, inputEnvelope, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            case SessionMessageType.ReplayRequest:
            {
                var replayRequest = DeserializeEnvelope<ReplayRequestPayload>(data, "replay request");
                var replayResponse = await _serviceProvider.GetRequiredService<ISessionOrchestrator>()
                    .ReplayAsync(replayRequest.SessionId, replayRequest, cancellationToken)
                    .ConfigureAwait(false);
                await _serviceProvider.GetRequiredService<IRelayPublisher>()
                    .PublishEnvelopeAsync(replayResponse, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            default:
                _logger.LogDebug(
                    "Ignoring unsupported client message type {MessageType} for session group {SessionGroup}.",
                    envelope.MessageType,
                    sessionGroup);
                return;
        }
    }

    private bool TryValidateSessionGroup<TPayload>(string sessionGroup, MessageEnvelope<TPayload> envelope)
    {
        string resolvedGroup;
        try
        {
            resolvedGroup = _sessionGroupResolver.Resolve(envelope);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(
                "Ignoring client envelope {MessageId} because its project/session ids could not resolve a session group: {FailureMessage}",
                envelope.MessageId,
                ex.Message);
            return false;
        }

        if (string.Equals(resolvedGroup, sessionGroup, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        _logger.LogWarning(
            "Ignoring client envelope {MessageId} for project {ProjectId} session {SessionId} because it arrived on {ActualSessionGroup} instead of {ExpectedSessionGroup}.",
            envelope.MessageId,
            envelope.ProjectId,
            envelope.SessionId,
            sessionGroup,
            resolvedGroup);
        return false;
    }

    private static MessageEnvelope<TPayload> DeserializeEnvelope<TPayload>(JsonElement data, string envelopeDescription)
    {
        var envelope = data.Deserialize<MessageEnvelope<TPayload>>(SessionMessageSerializer.DefaultOptions);
        if (envelope is null || envelope.Payload is null)
        {
            throw new InvalidOperationException($"Azure Web PubSub delivered a malformed {envelopeDescription} envelope.");
        }

        return envelope;
    }
}
