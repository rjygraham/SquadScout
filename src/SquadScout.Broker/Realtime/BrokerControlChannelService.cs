using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Azure.Messaging.WebPubSub;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SquadScout.Broker.Configuration;
using SquadScout.Contracts.Realtime;

namespace SquadScout.Broker.Realtime;

public sealed class BrokerControlChannelService : BackgroundService
{
    private const string WebPubSubSubprotocol = "json.webpubsub.azure.v1";
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly AzureWebPubSubOptions _options;
    private readonly BrokerControlMessageHandler _handler;
    private readonly ILogger<BrokerControlChannelService> _logger;

    public BrokerControlChannelService(
        IOptions<AzureWebPubSubOptions> options,
        BrokerControlMessageHandler handler,
        ILogger<BrokerControlChannelService> logger)
    {
        _options = options.Value;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            _logger.LogInformation("Broker control channel is disabled because AzureWebPubSub:ConnectionString is not configured.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunControlLoopAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Broker control channel disconnected. Reconnecting in {DelaySeconds} seconds.", ReconnectDelay.TotalSeconds);
                await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunControlLoopAsync(CancellationToken cancellationToken)
    {
        var sessionGroup = SessionGroupName.Create(BrokerControlChannel.ProjectId, BrokerControlChannel.SessionId);
        var roles = new[]
        {
            $"webpubsub.joinLeaveGroup.{sessionGroup}",
            $"webpubsub.sendToGroup.{sessionGroup}"
        };

        var serviceClient = new WebPubSubServiceClient(_options.ConnectionString, _options.Hub);
        var accessUri = await serviceClient.GetClientAccessUriAsync(
                DateTimeOffset.UtcNow.AddHours(12),
                $"broker:{sessionGroup}",
                roles,
                new[] { sessionGroup },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(WebPubSubSubprotocol);
        await socket.ConnectAsync(accessUri, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Broker control channel connected to {SessionGroup}.", sessionGroup);

        while (!cancellationToken.IsCancellationRequested)
        {
            var text = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                break;
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeProperty))
            {
                continue;
            }

            var type = typeProperty.GetString();
            if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("from", out var fromProperty) &&
                string.Equals(fromProperty.GetString(), "group", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("data", out var dataProperty))
            {
                await _handler.HandleAsync(dataProperty.Clone(), cancellationToken).ConfigureAwait(false);
            }
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
                throw new InvalidOperationException($"Broker control channel received unsupported message type '{result.MessageType}'.");
            }

            await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
}
