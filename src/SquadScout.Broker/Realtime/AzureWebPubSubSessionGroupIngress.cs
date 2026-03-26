using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Azure.Messaging.WebPubSub;
using Microsoft.Extensions.Logging;
using SquadScout.Broker.Configuration;
using SquadScout.Broker.Relay;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Realtime;

public sealed class AzureWebPubSubSessionGroupIngress : ISessionGroupIngress, IAsyncDisposable
{
    private const string WebPubSubSubprotocol = "json.webpubsub.azure.v1";
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly AzureWebPubSubOptions _options;
    private readonly ISessionGroupResolver _sessionGroupResolver;
    private readonly SessionGroupMessageHandler _handler;
    private readonly ILogger<AzureWebPubSubSessionGroupIngress> _logger;
    private readonly ConcurrentDictionary<string, SessionSubscription> _subscriptions = new(StringComparer.OrdinalIgnoreCase);

    public AzureWebPubSubSessionGroupIngress(
        AzureWebPubSubOptions options,
        ISessionGroupResolver sessionGroupResolver,
        SessionGroupMessageHandler handler,
        ILogger<AzureWebPubSubSessionGroupIngress> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sessionGroupResolver = sessionGroupResolver ?? throw new ArgumentNullException(nameof(sessionGroupResolver));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(SessionDescriptor session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            return Task.CompletedTask;
        }

        var subscription = new SessionSubscription(session.SessionId, _sessionGroupResolver.Resolve(session));
        if (!_subscriptions.TryAdd(session.SessionId, subscription))
        {
            return Task.CompletedTask;
        }

        subscription.RunTask = Task.Run(() => RunSessionLoopAsync(subscription), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sessionId) || !_subscriptions.TryRemove(sessionId, out var subscription))
        {
            return;
        }

        await subscription.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        var subscriptions = _subscriptions.ToArray();
        _subscriptions.Clear();

        foreach (var (_, subscription) in subscriptions)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunSessionLoopAsync(SessionSubscription subscription)
    {
        var serviceClient = new WebPubSubServiceClient(_options.ConnectionString!, _options.Hub);

        while (!subscription.CancellationToken.IsCancellationRequested)
        {
            try
            {
                var accessUri = await serviceClient.GetClientAccessUriAsync(
                        DateTimeOffset.UtcNow.AddHours(12),
                        $"broker:{subscription.SessionGroup}",
                        CreateScopedRoles(subscription.SessionGroup),
                        [subscription.SessionGroup],
                        cancellationToken: subscription.CancellationToken)
                    .ConfigureAwait(false);

                using var socket = new ClientWebSocket();
                socket.Options.AddSubProtocol(WebPubSubSubprotocol);
                await socket.ConnectAsync(accessUri, subscription.CancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Broker live session channel connected to {SessionGroup}.", subscription.SessionGroup);

                while (!subscription.CancellationToken.IsCancellationRequested)
                {
                    var text = await ReceiveTextAsync(socket, subscription.CancellationToken).ConfigureAwait(false);
                    if (text is null)
                    {
                        break;
                    }

                    await ProcessIncomingMessageAsync(subscription.SessionGroup, text, subscription.CancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (subscription.CancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Broker live session channel disconnected for {SessionGroup}. Reconnecting in {DelaySeconds} seconds.",
                    subscription.SessionGroup,
                    ReconnectDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(ReconnectDelay, subscription.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (subscription.CancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ProcessIncomingMessageAsync(string sessionGroup, string text, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeProperty))
        {
            return;
        }

        if (!string.Equals(typeProperty.GetString(), "message", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("from", out var fromProperty) ||
            !string.Equals(fromProperty.GetString(), "group", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("dataType", out var dataTypeProperty) ||
            !string.Equals(dataTypeProperty.GetString(), "json", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("data", out var dataProperty))
        {
            return;
        }

        try
        {
            await _handler.HandleAsync(sessionGroup, dataProperty.Clone(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Failed to process session group message for {SessionGroup}.",
                sessionGroup);
        }
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidOperationException($"Broker live session channel received unsupported message type '{result.MessageType}'.");
            }

            await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static string[] CreateScopedRoles(string sessionGroup) =>
    [
        $"webpubsub.joinLeaveGroup.{sessionGroup}",
        $"webpubsub.sendToGroup.{sessionGroup}"
    ];

    private sealed class SessionSubscription(string sessionId, string sessionGroup) : IAsyncDisposable
    {
        public string SessionId { get; } = sessionId;

        public string SessionGroup { get; } = sessionGroup;

        public CancellationTokenSource CancellationTokenSource { get; } = new();

        public CancellationToken CancellationToken => CancellationTokenSource.Token;

        public Task RunTask { get; set; } = Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            CancellationTokenSource.Cancel();
            try
            {
                await RunTask.ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                CancellationTokenSource.Dispose();
            }
        }
    }
}
