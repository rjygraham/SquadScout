using System.Net.WebSockets;
using System.Text;

namespace SquadScout.App.Services;

public interface IWebPubSubSocketFactory
{
    IWebPubSubSocket Create();
}

public interface IWebPubSubSocket : IAsyncDisposable
{
    WebSocketState State { get; }

    Task ConnectAsync(Uri uri, string subprotocol, CancellationToken cancellationToken = default);

    Task SendTextAsync(string text, CancellationToken cancellationToken = default);

    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken = default);

    Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken = default);
}

public sealed class ClientWebPubSubSocketFactory : IWebPubSubSocketFactory
{
    public IWebPubSubSocket Create() => new ClientWebPubSubSocket(new ClientWebSocket());
}

internal sealed class ClientWebPubSubSocket : IWebPubSubSocket
{
    private readonly ClientWebSocket _socket;

    public ClientWebPubSubSocket(ClientWebSocket socket)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
    }

    public WebSocketState State => _socket.State;

    public async Task ConnectAsync(Uri uri, string subprotocol, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(subprotocol);

        if (_socket.State != WebSocketState.None)
        {
            throw new InvalidOperationException("The Web PubSub socket has already been used and cannot connect again.");
        }

        _socket.Options.AddSubProtocol(subprotocol);
        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var payload = Encoding.UTF8.GetBytes(text);
        return _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken = default)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidOperationException($"Web PubSub returned unsupported message type '{result.MessageType}'.");
            }

            await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    public async Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken = default)
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        await _socket.CloseAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
