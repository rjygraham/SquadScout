using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Relay;

public sealed class AzureWebPubSubRelayPublisher : IRelayPublisher
{
    private readonly IWebPubSubGroupClient _groupClient;
    private readonly ISessionGroupResolver _sessionGroupResolver;
    private readonly ILogger<AzureWebPubSubRelayPublisher> _logger;
    private readonly ConcurrentDictionary<string, string> _activeSessionGroups = new(StringComparer.OrdinalIgnoreCase);

    public AzureWebPubSubRelayPublisher(
        IWebPubSubGroupClient groupClient,
        ISessionGroupResolver sessionGroupResolver,
        ILogger<AzureWebPubSubRelayPublisher> logger)
    {
        _groupClient = groupClient ?? throw new ArgumentNullException(nameof(groupClient));
        _sessionGroupResolver = sessionGroupResolver ?? throw new ArgumentNullException(nameof(sessionGroupResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task PublishSessionStartedAsync(SessionDescriptor session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);

        var sessionGroup = _sessionGroupResolver.Resolve(session);
        _activeSessionGroups[session.SessionId] = sessionGroup;

        _logger.LogInformation(
            "Broker joined session group {SessionGroup} for project {ProjectId} session {SessionId}.",
            sessionGroup,
            session.ProjectId,
            session.SessionId);

        return Task.CompletedTask;
    }

    public async Task PublishEnvelopeAsync<TPayload>(MessageEnvelope<TPayload> envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(envelope);

        var sessionGroup = _activeSessionGroups.GetOrAdd(envelope.SessionId, _ => _sessionGroupResolver.Resolve(envelope));
        var payload = JsonSerializer.Serialize(envelope, SessionMessageSerializer.DefaultOptions);
        await _groupClient.SendJsonToGroupAsync(sessionGroup, payload, cancellationToken).ConfigureAwait(false);

        if (IsStoppedLifecycle(envelope))
        {
            _activeSessionGroups.TryRemove(envelope.SessionId, out _);
            _logger.LogInformation(
                "Broker left session group {SessionGroup} for project {ProjectId} session {SessionId}.",
                sessionGroup,
                envelope.ProjectId,
                envelope.SessionId);
        }
    }

    private static bool IsStoppedLifecycle<TPayload>(MessageEnvelope<TPayload> envelope) =>
        envelope.MessageType == SessionMessageType.SessionLifecycle
        && envelope.Payload is SessionLifecyclePayload lifecycle
        && lifecycle.State == SessionState.Stopped;
}
