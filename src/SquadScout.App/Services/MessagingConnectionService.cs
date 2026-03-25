using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using SquadScout.App.Configuration;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Realtime;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.Services;

public sealed class MessagingConnectionService : IMessageConnectionService, IAsyncDisposable
{
    private const string WebPubSubSubprotocol = "json.webpubsub.azure.v1";

    private readonly MessagingOptions _messagingOptions;
    private readonly IPubSubNegotiationClient _negotiationClient;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<WebPubSubAckMessage>> _pendingAcks = new();
    private readonly object _stateSync = new();
    private readonly IWebPubSubSocketFactory _socketFactory;

    private SessionDescriptor? _activeSession;
    private PubSubNegotiateResponse? _negotiation;
    private IWebPubSubSocket? _socket;
    private CancellationTokenSource? _receiveLoopCts;
    private Task? _receiveLoopTask;
    private MessageConnectionStatus _currentStatus;
    private MessageEnvelopeTraffic[] _recentTraffic = [];
    private string? _connectionId;
    private long _nextAckId;
    private long _nextClientSequence;
    private long _currentGeneration = SessionEnvelopeContract.InitialGeneration;
    private long? _acknowledgedSequence;
    private bool _gapDetected;

    public MessagingConnectionService(
        MessagingOptions messagingOptions,
        IPubSubNegotiationClient negotiationClient,
        IWebPubSubSocketFactory socketFactory)
    {
        _messagingOptions = messagingOptions ?? throw new ArgumentNullException(nameof(messagingOptions));
        _negotiationClient = negotiationClient ?? throw new ArgumentNullException(nameof(negotiationClient));
        _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
        _currentStatus = CreateDisconnectedStatus();
    }

    public event EventHandler<MessageConnectionStatus>? StatusChanged;

    public event EventHandler<MessageEnvelopeTraffic>? TrafficObserved;

    public MessageConnectionStatus CurrentStatus
    {
        get
        {
            lock (_stateSync)
            {
                return _currentStatus;
            }
        }
    }

    public IReadOnlyList<MessageEnvelopeTraffic> RecentTraffic
    {
        get
        {
            lock (_stateSync)
            {
                return _recentTraffic;
            }
        }
    }

    public async Task<MessageConnectionStatus> PrepareForSessionAsync(SessionDescriptor session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var isSameSession = IsCurrentSession(session);
            if (!isSameSession)
            {
                await CleanupTransportAsync(cancellationToken).ConfigureAwait(false);
                SetActiveSession(session);
                ResetTransportState(clearTraffic: true);
            }

            if (!_messagingOptions.AutoPrepareOnSessionStart)
            {
                if (isSameSession)
                {
                    await CleanupTransportAsync(cancellationToken).ConfigureAwait(false);
                }

                var readyStatus = CreateReadyStatus(session);
                PublishStatus(readyStatus);
                return readyStatus;
            }

            if (isSameSession && CurrentStatus.State == MessageConnectionState.Connected)
            {
                return CurrentStatus;
            }

            return await ConnectCoreAsync(isReconnect: false, reconnectAttempt: 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<MessageConnectionStatus> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = GetActiveSession();
            if (session is null)
            {
                var status = CreateDisconnectedStatus("No active session is available for live messaging.");
                PublishStatus(status);
                return status;
            }

            return await ConnectCoreAsync(isReconnect: true, reconnectAttempt: CurrentStatus.ReconnectAttempt + 1, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task SendInputAsync(string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Input content is required before it can be sent to the broker.", nameof(content));
        }

        MessageEnvelope<InputChunkPayload> envelope;
        lock (_stateSync)
        {
            if (_activeSession is null)
            {
                throw new InvalidOperationException("An active session is required before input can be sent.");
            }

            var nextClientSequence = ++_nextClientSequence;
            envelope = new MessageEnvelope<InputChunkPayload>
            {
                ProjectId = _activeSession.ProjectId,
                SessionId = _activeSession.SessionId,
                Generation = _currentGeneration,
                MessageType = SessionMessageType.Input,
                Direction = MessageDirection.ClientToBroker,
                ClientSequence = nextClientSequence,
                AcknowledgedSequence = _acknowledgedSequence,
                TimestampUtc = DateTimeOffset.UtcNow,
                MessageId = $"client-{_activeSession.SessionId}-{nextClientSequence}",
                CorrelationId = $"mobile-session:{_activeSession.SessionId}",
                Payload = new InputChunkPayload
                {
                    Content = content
                }
            };
        }

        return SendEnvelopeAsync(envelope, cancellationToken);
    }

    public async Task SendEnvelopeAsync<TPayload>(MessageEnvelope<TPayload> envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.Payload);
        cancellationToken.ThrowIfCancellationRequested();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (session, negotiation, socket) = GetConnectedTransport();
            EnsureEnvelopeTargetsSession(envelope, session);

            if (envelope.Direction != MessageDirection.ClientToBroker)
            {
                throw new InvalidOperationException("Only client-to-broker envelopes can be sent from the mobile transport.");
            }

            var payload = JsonSerializer.SerializeToElement(envelope, SessionMessageSerializer.DefaultOptions);
            var ackId = Interlocked.Increment(ref _nextAckId);
            await SendCommandExpectAckAsync(
                    socket,
                    new WebPubSubSendToGroupCommand
                    {
                        Group = negotiation.SessionGroup,
                        DataType = "json",
                        Data = payload,
                        NoEcho = true,
                        AckId = ackId
                    },
                    ackId,
                    "send session envelope",
                    treatDuplicateAsSuccess: false,
                    cancellationToken)
                .ConfigureAwait(false);

            AppendTraffic(
                new MessageEnvelopeTraffic
                {
                    Direction = MessageTrafficDirection.Outgoing,
                    Envelope = ToJsonEnvelope(envelope),
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                    Summary = $"Sent {envelope.MessageType} for session '{session.SessionId}'."
                });
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<MessageConnectionStatus> ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CleanupTransportAsync(cancellationToken).ConfigureAwait(false);

            lock (_stateSync)
            {
                _activeSession = null;
                ResetTransportState(clearTraffic: true);
            }

            var status = CreateDisconnectedStatus();
            PublishStatus(status);
            return status;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync().ConfigureAwait(false);
        _operationGate.Dispose();
    }

    private async Task<MessageConnectionStatus> ConnectCoreAsync(
        bool isReconnect,
        int reconnectAttempt,
        CancellationToken cancellationToken)
    {
        var session = GetActiveSession()
            ?? throw new InvalidOperationException("An active session is required before live messaging can connect.");

        await CleanupTransportAsync(cancellationToken).ConfigureAwait(false);

        PublishStatus(
            new MessageConnectionStatus
            {
                State = isReconnect ? MessageConnectionState.Reconnecting : MessageConnectionState.Connecting,
                Summary = isReconnect
                    ? $"Retrying live messaging for session '{session.SessionId}'."
                    : $"Connecting live messaging for session '{session.SessionId}'.",
                Hub = _messagingOptions.Hub,
                SupportsLiveSessionStream = true,
                ProjectId = session.ProjectId,
                SessionId = session.SessionId,
                ReconnectAttempt = reconnectAttempt
            });

        try
        {
            var negotiation = await _negotiationClient.NegotiateAsync(session, cancellationToken).ConfigureAwait(false);
            var socket = _socketFactory.Create();

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _messagingOptions.ConnectTimeoutSeconds)));

            await socket.ConnectAsync(new Uri(negotiation.Url), WebPubSubSubprotocol, connectCts.Token).ConfigureAwait(false);

            var connectedTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var receiveLoopCts = new CancellationTokenSource();
            var receiveLoopTask = Task.Run(
                () => ReceiveLoopAsync(socket, connectedTcs, receiveLoopCts.Token),
                CancellationToken.None);

            lock (_stateSync)
            {
                _negotiation = negotiation;
                _socket = socket;
                _receiveLoopCts = receiveLoopCts;
                _receiveLoopTask = receiveLoopTask;
            }

            var connectionId = await connectedTcs.Task.WaitAsync(connectCts.Token).ConfigureAwait(false);
            await SendJoinGroupAsync(socket, negotiation.SessionGroup, cancellationToken).ConfigureAwait(false);

            lock (_stateSync)
            {
                _connectionId = connectionId;
            }

            var status = new MessageConnectionStatus
            {
                State = MessageConnectionState.Connected,
                Summary = isReconnect
                    ? $"Live messaging reconnected for session '{session.SessionId}' on hub '{negotiation.Hub}'."
                    : $"Live messaging connected for session '{session.SessionId}' on hub '{negotiation.Hub}'.",
                Hub = negotiation.Hub,
                SupportsLiveSessionStream = true,
                ProjectId = session.ProjectId,
                SessionId = session.SessionId,
                SessionGroup = negotiation.SessionGroup,
                ConnectionId = connectionId,
                ConnectedAtUtc = DateTimeOffset.UtcNow,
                RefreshAtUtc = negotiation.RefreshAtUtc,
                ReconnectAttempt = reconnectAttempt
            };

            PublishStatus(status);
            return status;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await CleanupTransportAsync(CancellationToken.None).ConfigureAwait(false);
            var timeoutStatus = CreateFaultedStatus(
                session,
                reconnectAttempt,
                "Connecting to Azure Web PubSub timed out. Retry the live transport when the function and service are reachable.");
            PublishStatus(timeoutStatus);
            return timeoutStatus;
        }
        catch (Exception ex)
        {
            await CleanupTransportAsync(CancellationToken.None).ConfigureAwait(false);
            var failureStatus = CreateFaultedStatus(session, reconnectAttempt, ex.Message);
            PublishStatus(failureStatus);
            return failureStatus;
        }
    }

    private async Task ReceiveLoopAsync(
        IWebPubSubSocket socket,
        TaskCompletionSource<string?> connectedTcs,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await socket.ReceiveTextAsync(cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    var disconnectMessage = "The live session stream disconnected. Use Retry live transport to reconnect.";
                    connectedTcs.TrySetException(new InvalidOperationException(disconnectMessage));
                    PublishReceiveFault(disconnectMessage);
                    return;
                }

                if (!await ProcessIncomingMessageAsync(message, connectedTcs, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            connectedTcs.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            connectedTcs.TrySetException(ex);
            PublishReceiveFault($"The live session stream failed: {ex.Message}");
        }
    }

    private async Task<bool> ProcessIncomingMessageAsync(
        string message,
        TaskCompletionSource<string?> connectedTcs,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString();

        switch (type)
        {
            case "system":
                return ProcessSystemMessage(root, connectedTcs);

            case "ack":
                ProcessAckMessage(root);
                return true;

            case "message":
                await ProcessGroupMessageAsync(root, cancellationToken).ConfigureAwait(false);
                return true;

            default:
                PublishReceiveFault($"The live session stream returned unsupported message type '{type}'.");
                return true;
        }
    }

    private bool ProcessSystemMessage(JsonElement root, TaskCompletionSource<string?> connectedTcs)
    {
        var @event = root.GetProperty("event").GetString();
        switch (@event)
        {
            case "connected":
                var connectionId = root.TryGetProperty("connectionId", out var connectionIdElement)
                    ? connectionIdElement.GetString()
                    : null;
                connectedTcs.TrySetResult(connectionId);
                return true;

            case "disconnected":
                var disconnectMessage = "Azure Web PubSub closed the session stream. Use Retry live transport to reconnect.";
                connectedTcs.TrySetException(new InvalidOperationException(disconnectMessage));
                PublishReceiveFault(disconnectMessage);
                return false;

            default:
                return true;
        }
    }

    private void ProcessAckMessage(JsonElement root)
    {
        var ack = new WebPubSubAckMessage
        {
            AckId = root.GetProperty("ackId").GetInt64(),
            Success = root.GetProperty("success").GetBoolean(),
            ErrorName = root.TryGetProperty("error", out var errorElement) &&
                        errorElement.TryGetProperty("name", out var errorNameElement)
                ? errorNameElement.GetString()
                : null,
            ErrorMessage = root.TryGetProperty("error", out errorElement) &&
                           errorElement.TryGetProperty("message", out var errorMessageElement)
                ? errorMessageElement.GetString()
                : null
        };

        if (_pendingAcks.TryRemove(ack.AckId, out var tcs))
        {
            tcs.TrySetResult(ack);
        }
    }

    private Task ProcessGroupMessageAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("dataType", out var dataTypeElement) ||
            !string.Equals(dataTypeElement.GetString(), "json", StringComparison.OrdinalIgnoreCase))
        {
            PublishReceiveFault("The live session stream returned a non-JSON group message, which the mobile transport cannot process.");
            return Task.CompletedTask;
        }

        var data = root.GetProperty("data").Clone();
        var envelope = data.Deserialize<MessageEnvelope<JsonElement>>(SessionMessageSerializer.DefaultOptions)
            ?? throw new InvalidOperationException("Azure Web PubSub delivered a malformed session envelope.");

        lock (_stateSync)
        {
            if (envelope.Generation != _currentGeneration)
            {
                _currentGeneration = envelope.Generation;
                _acknowledgedSequence = null;
                _gapDetected = false;
            }

            if (envelope.Sequence is long sequence)
            {
                var expectedSequence = (_acknowledgedSequence ?? 0) + 1;
                if (sequence == expectedSequence)
                {
                    _acknowledgedSequence = sequence;
                }
                else if (sequence > expectedSequence)
                {
                    _gapDetected = true;
                }
            }
        }

        AppendTraffic(
            new MessageEnvelopeTraffic
            {
                Direction = MessageTrafficDirection.Incoming,
                Envelope = envelope,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Summary = $"Received {envelope.MessageType} for session '{envelope.SessionId}'."
            });

        if (_gapDetected)
        {
            PublishStatus(
                CurrentStatus with
                {
                    Summary =
                        $"Live messaging is connected, but a broker sequence gap was detected for session '{envelope.SessionId}'. Retry the transport before trusting transcript continuity."
                });
        }

        return Task.CompletedTask;
    }

    private async Task SendJoinGroupAsync(IWebPubSubSocket socket, string sessionGroup, CancellationToken cancellationToken)
    {
        var ackId = Interlocked.Increment(ref _nextAckId);
        await SendCommandExpectAckAsync(
                socket,
                new WebPubSubJoinGroupCommand
                {
                    Group = sessionGroup,
                    AckId = ackId
                },
                ackId,
                "join the negotiated session group",
                treatDuplicateAsSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendCommandExpectAckAsync(
        IWebPubSubSocket socket,
        object command,
        long ackId,
        string operation,
        bool treatDuplicateAsSuccess,
        CancellationToken cancellationToken)
    {
        var ackTcs = new TaskCompletionSource<WebPubSubAckMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAcks[ackId] = ackTcs;

        try
        {
            var payload = JsonSerializer.Serialize(command, SessionMessageSerializer.DefaultOptions);
            await socket.SendTextAsync(payload, cancellationToken).ConfigureAwait(false);

            using var ackTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ackTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _messagingOptions.CommandAckTimeoutSeconds)));
            var ack = await ackTcs.Task.WaitAsync(ackTimeout.Token).ConfigureAwait(false);

            if (!ack.Success &&
                !(treatDuplicateAsSuccess &&
                  string.Equals(ack.ErrorName, "Duplicate", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Azure Web PubSub could not {operation}: {ack.ErrorMessage ?? ack.ErrorName ?? "Unknown acknowledgement failure."}");
            }
        }
        finally
        {
            _pendingAcks.TryRemove(ackId, out _);
        }
    }

    private async Task CleanupTransportAsync(CancellationToken cancellationToken)
    {
        IWebPubSubSocket? socket;
        CancellationTokenSource? receiveLoopCts;
        Task? receiveLoopTask;

        lock (_stateSync)
        {
            socket = _socket;
            receiveLoopCts = _receiveLoopCts;
            receiveLoopTask = _receiveLoopTask;
            _socket = null;
            _receiveLoopCts = null;
            _receiveLoopTask = null;
            _connectionId = null;
        }

        foreach (var (_, pendingAck) in _pendingAcks.ToArray())
        {
            pendingAck.TrySetException(new InvalidOperationException("The live transport disconnected before Azure Web PubSub acknowledged the operation."));
        }

        _pendingAcks.Clear();
        receiveLoopCts?.Cancel();

        if (socket is not null)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "SquadScout disconnect", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }

            await socket.DisposeAsync().ConfigureAwait(false);
        }

        if (receiveLoopTask is not null)
        {
            try
            {
                await receiveLoopTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        receiveLoopCts?.Dispose();
    }

    private (SessionDescriptor Session, PubSubNegotiateResponse Negotiation, IWebPubSubSocket Socket) GetConnectedTransport()
    {
        lock (_stateSync)
        {
            if (_activeSession is null || _negotiation is null || _socket is null || CurrentStatus.State != MessageConnectionState.Connected)
            {
                throw new InvalidOperationException("Live messaging is not connected. Use Retry live transport to reconnect before sending.");
            }

            return (_activeSession, _negotiation, _socket);
        }
    }

    private SessionDescriptor? GetActiveSession()
    {
        lock (_stateSync)
        {
            return _activeSession;
        }
    }

    private bool IsCurrentSession(SessionDescriptor session)
    {
        lock (_stateSync)
        {
            return _activeSession is not null &&
                   string.Equals(_activeSession.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(_activeSession.ProjectId, session.ProjectId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void SetActiveSession(SessionDescriptor session)
    {
        lock (_stateSync)
        {
            _activeSession = session;
        }
    }

    private void ResetTransportState(bool clearTraffic)
    {
        lock (_stateSync)
        {
            _negotiation = null;
            _connectionId = null;
            _currentGeneration = SessionEnvelopeContract.InitialGeneration;
            _acknowledgedSequence = null;
            _gapDetected = false;
            _nextAckId = 0;
            _nextClientSequence = 0;
            if (clearTraffic)
            {
                _recentTraffic = [];
            }
        }
    }

    private void EnsureEnvelopeTargetsSession<TPayload>(MessageEnvelope<TPayload> envelope, SessionDescriptor session)
    {
        if (!string.Equals(envelope.ProjectId, session.ProjectId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(envelope.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Envelope '{envelope.MessageId}' targeted {envelope.ProjectId}/{envelope.SessionId}, but the active mobile session is {session.ProjectId}/{session.SessionId}.");
        }
    }

    private void PublishReceiveFault(string message)
    {
        var session = GetActiveSession();
        if (session is null)
        {
            PublishStatus(CreateDisconnectedStatus(message));
            return;
        }

        PublishStatus(CreateFaultedStatus(session, CurrentStatus.ReconnectAttempt, message));
    }

    private void PublishStatus(MessageConnectionStatus status)
    {
        lock (_stateSync)
        {
            _currentStatus = status;
        }

        StatusChanged?.Invoke(this, status);
    }

    private void AppendTraffic(MessageEnvelopeTraffic traffic)
    {
        lock (_stateSync)
        {
            var updated = _recentTraffic.Concat([traffic]).TakeLast(Math.Max(1, _messagingOptions.RecentTrafficCapacity)).ToArray();
            _recentTraffic = updated;
        }

        TrafficObserved?.Invoke(this, traffic);
    }

    private MessageConnectionStatus CreateReadyStatus(SessionDescriptor session) =>
        new()
        {
            State = MessageConnectionState.Ready,
            Summary = $"Live messaging is staged for session '{session.SessionId}'. Use Retry live transport to connect when needed.",
            Hub = _messagingOptions.Hub,
            SupportsLiveSessionStream = true,
            ProjectId = session.ProjectId,
            SessionId = session.SessionId
        };

    private MessageConnectionStatus CreateDisconnectedStatus(string? summary = null) =>
        new()
        {
            State = MessageConnectionState.Disconnected,
            Summary = summary ?? $"Live messaging is disconnected from hub '{_messagingOptions.Hub}'.",
            Hub = _messagingOptions.Hub,
            SupportsLiveSessionStream = true
        };

    private MessageConnectionStatus CreateFaultedStatus(
        SessionDescriptor session,
        int reconnectAttempt,
        string failureReason) =>
        new()
        {
            State = MessageConnectionState.Faulted,
            Summary = $"Live messaging is unavailable for session '{session.SessionId}'. {failureReason}",
            Hub = _negotiation?.Hub ?? _messagingOptions.Hub,
            SupportsLiveSessionStream = true,
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            SessionGroup = _negotiation?.SessionGroup,
            RefreshAtUtc = _negotiation?.RefreshAtUtc,
            ReconnectAttempt = reconnectAttempt,
            FailureReason = failureReason
        };

    private static MessageEnvelope<JsonElement> ToJsonEnvelope<TPayload>(MessageEnvelope<TPayload> envelope) =>
        new()
        {
            ContractVersion = envelope.ContractVersion,
            ProjectId = envelope.ProjectId,
            SessionId = envelope.SessionId,
            Generation = envelope.Generation,
            MessageType = envelope.MessageType,
            Direction = envelope.Direction,
            Sequence = envelope.Sequence,
            ClientSequence = envelope.ClientSequence,
            AcknowledgedSequence = envelope.AcknowledgedSequence,
            TimestampUtc = envelope.TimestampUtc,
            MessageId = envelope.MessageId,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            Payload = envelope.Payload is JsonElement jsonPayload
                ? jsonPayload.Clone()
                : JsonSerializer.SerializeToElement(envelope.Payload, SessionMessageSerializer.DefaultOptions)
        };

    private sealed record WebPubSubJoinGroupCommand
    {
        public string Type { get; init; } = "joinGroup";

        public string Group { get; init; } = string.Empty;

        public long AckId { get; init; }
    }

    private sealed record WebPubSubSendToGroupCommand
    {
        public string Type { get; init; } = "sendToGroup";

        public string Group { get; init; } = string.Empty;

        public string DataType { get; init; } = "json";

        public JsonElement Data { get; init; }

        public bool NoEcho { get; init; }

        public long AckId { get; init; }
    }

    private sealed record WebPubSubAckMessage
    {
        public long AckId { get; init; }

        public bool Success { get; init; }

        public string? ErrorName { get; init; }

        public string? ErrorMessage { get; init; }
    }
}
