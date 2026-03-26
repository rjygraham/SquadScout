using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using SquadScout.App.Configuration;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Realtime;
using SquadScout.Contracts.Security;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.Services;

public sealed class MessagingConnectionService : IMessageConnectionService, IAsyncDisposable
{
    private const string WebPubSubSubprotocol = "json.webpubsub.azure.v1";
    private static readonly TimeSpan TokenRefreshLeadTime = TimeSpan.FromMinutes(5);

    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly MessagingOptions _messagingOptions;
    private readonly IPubSubNegotiationClient _negotiationClient;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<WebPubSubAckMessage>> _pendingAcks = new();
    private readonly object _stateSync = new();
    private readonly IWebPubSubSocketFactory _socketFactory;
    private readonly Func<DateTimeOffset> _utcNow;

    private SessionDescriptor? _activeSession;
    private PubSubNegotiateResponse? _negotiation;
    private CancellationTokenSource? _refreshLoopCts;
    private Task? _refreshLoopTask;
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
    private long? _highestObservedBrokerSequence;
    private readonly HashSet<long> _observedBrokerSequences = new();
    private readonly HashSet<long> _appliedBrokerSequences = new();
    private readonly HashSet<string> _appliedReplayResponseMessageIds = new(StringComparer.Ordinal);
    private bool _gapDetected;
    private bool _replayRequestPending;

    public MessagingConnectionService(
        MessagingOptions messagingOptions,
        IPubSubNegotiationClient negotiationClient,
        IWebPubSubSocketFactory socketFactory)
        : this(
            messagingOptions,
            negotiationClient,
            socketFactory,
            static () => DateTimeOffset.UtcNow,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    internal MessagingConnectionService(
        MessagingOptions messagingOptions,
        IPubSubNegotiationClient negotiationClient,
        IWebPubSubSocketFactory socketFactory,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _messagingOptions = messagingOptions ?? throw new ArgumentNullException(nameof(messagingOptions));
        _negotiationClient = negotiationClient ?? throw new ArgumentNullException(nameof(negotiationClient));
        _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
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
            await SendEnvelopeCoreAsync(envelope, requireConnectedStatus: true, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        PubSubNegotiateResponse? negotiationOverride = null,
        bool publishTransitionStatus = true,
        string? failureReasonPrefix = null)
    {
        var session = GetActiveSession()
            ?? throw new InvalidOperationException("An active session is required before live messaging can connect.");

        await CleanupTransportAsync(cancellationToken).ConfigureAwait(false);

        if (publishTransitionStatus)
        {
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
        }

        try
        {
            var negotiation = negotiationOverride
                              ?? await _negotiationClient.NegotiateAsync(session, cancellationToken).ConfigureAwait(false);
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
                ConnectedAtUtc = _utcNow(),
                RefreshAtUtc = negotiation.RefreshAtUtc,
                ReconnectAttempt = reconnectAttempt
            };

            PublishStatus(status);
            ScheduleTokenRefresh(negotiation);
            if (isReconnect)
            {
                await RequestReplayAsync(
                        ReplayRequestReason.ReconnectResume,
                        operationGateHeld: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return status;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await CleanupTransportAsync(CancellationToken.None).ConfigureAwait(false);
            var timeoutStatus = CreateFaultedStatus(
                session,
                reconnectAttempt,
                ComposeFailureReason(
                    failureReasonPrefix,
                    "Connecting to Azure Web PubSub timed out. Retry the live transport when the function and service are reachable."));
            PublishStatus(timeoutStatus);
            return timeoutStatus;
        }
        catch (Exception ex)
        {
            await CleanupTransportAsync(CancellationToken.None).ConfigureAwait(false);
            var failureStatus = CreateFaultedStatus(
                session,
                reconnectAttempt,
                ComposeFailureReason(failureReasonPrefix, ex.Message));
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
                return await ProcessGroupMessageAsync(root, cancellationToken).ConfigureAwait(false);

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

    private async Task<bool> ProcessGroupMessageAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("dataType", out var dataTypeElement) ||
            !string.Equals(dataTypeElement.GetString(), "json", StringComparison.OrdinalIgnoreCase))
        {
            PublishReceiveFault("The live session stream returned a non-JSON group message, which the mobile transport cannot process.");
            return false;
        }

        var data = root.GetProperty("data").Clone();
        var envelope = data.Deserialize<MessageEnvelope<JsonElement>>(SessionMessageSerializer.DefaultOptions)
            ?? throw new InvalidOperationException("Azure Web PubSub delivered a malformed session envelope.");

        if (TryRejectStaleBrokerEnvelope(envelope, $"incoming {envelope.MessageType} envelope"))
        {
            return false;
        }

        var tracking = TrackBrokerEnvelope(envelope);
        if (!tracking.ShouldProcess)
        {
            return true;
        }

        if (envelope.MessageType == SessionMessageType.ReplayResponse)
        {
            return await ApplyReplayResponseAsync(envelope, tracking, cancellationToken).ConfigureAwait(false);
        }

        if (tracking.ShouldAppend)
        {
            AppendTraffic(
                new MessageEnvelopeTraffic
                {
                    Direction = MessageTrafficDirection.Incoming,
                    Envelope = ToJsonEnvelope(envelope),
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                    Summary = $"Received {envelope.MessageType} for session '{envelope.SessionId}'."
                });
        }

        if (tracking.GapJustDetected)
        {
            PublishStatus(
                CurrentStatus with
                {
                    Summary =
                        $"Live messaging detected a broker sequence gap for session '{envelope.SessionId}'. Requesting replay before trusting transcript continuity."
                });

            QueueReplayRequest(ReplayRequestReason.GapDetected, cancellationToken);
        }

        return true;
    }

    private async Task<bool> ApplyReplayResponseAsync(
        MessageEnvelope<JsonElement> envelope,
        BrokerEnvelopeTrackingResult tracking,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payload = envelope.Payload.Deserialize<ReplayResponsePayload>(SessionMessageSerializer.DefaultOptions)
            ?? throw new InvalidOperationException("Azure Web PubSub delivered a malformed replay response payload.");

        lock (_stateSync)
        {
            _replayRequestPending = false;
        }

        if (tracking.ShouldAppend)
        {
            AppendTraffic(
                new MessageEnvelopeTraffic
                {
                    Direction = MessageTrafficDirection.Incoming,
                    Envelope = ToJsonEnvelope(envelope),
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                    Summary = $"Received replay response for session '{envelope.SessionId}'."
                });
        }

        if (payload.Generation != envelope.Generation)
        {
            PublishInvalidReplayGenerationBoundary(
                envelope,
                payload.Generation,
                envelope.Generation,
                "replay payload");
            return false;
        }

        if (payload.GapDetected)
        {
            PublishStatus(CreateReplayGapWarningStatus(envelope.SessionId, payload));
        }

        foreach (var replayedEnvelope in payload.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (replayedEnvelope.Generation != envelope.Generation)
            {
                PublishInvalidReplayGenerationBoundary(
                    replayedEnvelope,
                    replayedEnvelope.Generation,
                    envelope.Generation,
                    $"replayed {replayedEnvelope.MessageType} envelope");
                return false;
            }

            var replayTracking = TrackBrokerEnvelope(replayedEnvelope);
            if (!replayTracking.ShouldProcess || !replayTracking.ShouldAppend)
            {
                continue;
            }

            AppendTraffic(
                new MessageEnvelopeTraffic
                {
                    Direction = MessageTrafficDirection.Incoming,
                    Envelope = ToJsonEnvelope(replayedEnvelope),
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                    Summary = $"Applied replayed {replayedEnvelope.MessageType} for session '{replayedEnvelope.SessionId}'."
                });
        }

        if (!payload.GapDetected && !IsGapDetected())
        {
            var restoredStatus = TryCreateConnectedStatus();
            if (restoredStatus is not null)
            {
                PublishStatus(restoredStatus);
            }
        }

        return true;
    }

    private BrokerEnvelopeTrackingResult TrackBrokerEnvelope(MessageEnvelope<JsonElement> envelope)
    {
        lock (_stateSync)
        {
            if (_activeSession is null || !MatchesActiveSessionLocked(envelope.ProjectId, envelope.SessionId))
            {
                return new BrokerEnvelopeTrackingResult(ShouldProcess: false, ShouldAppend: false, GapJustDetected: false);
            }

            if (envelope.Generation < _currentGeneration)
            {
                return new BrokerEnvelopeTrackingResult(ShouldProcess: false, ShouldAppend: false, GapJustDetected: false);
            }

            if (envelope.Generation > _currentGeneration)
            {
                ResetBrokerOrderingStateLocked(envelope.Generation);
            }

            if (envelope.Sequence is not long sequence)
            {
                if (envelope.MessageType == SessionMessageType.ReplayResponse &&
                    !string.IsNullOrWhiteSpace(envelope.MessageId))
                {
                    var shouldProcessReplayResponse = _appliedReplayResponseMessageIds.Add(envelope.MessageId);
                    return new BrokerEnvelopeTrackingResult(
                        ShouldProcess: shouldProcessReplayResponse,
                        ShouldAppend: shouldProcessReplayResponse,
                        GapJustDetected: false);
                }

                return new BrokerEnvelopeTrackingResult(ShouldProcess: true, ShouldAppend: true, GapJustDetected: false);
            }

            var expectedSequence = (_acknowledgedSequence ?? 0) + 1;
            var isNewSequence = _observedBrokerSequences.Add(sequence);
            _highestObservedBrokerSequence = Math.Max(_highestObservedBrokerSequence ?? 0, sequence);

            var gapJustDetected = isNewSequence &&
                                  sequence > expectedSequence &&
                                  !_gapDetected &&
                                  !_replayRequestPending;

            AdvanceAcknowledgedSequenceLocked();
            _gapDetected = (_acknowledgedSequence ?? 0) < (_highestObservedBrokerSequence ?? 0);

            return new BrokerEnvelopeTrackingResult(
                ShouldProcess: true,
                ShouldAppend: _appliedBrokerSequences.Add(sequence),
                GapJustDetected: gapJustDetected);
        }
    }

    private void AdvanceAcknowledgedSequenceLocked()
    {
        var nextSequence = (_acknowledgedSequence ?? 0) + 1;
        while (_observedBrokerSequences.Contains(nextSequence))
        {
            _acknowledgedSequence = nextSequence;
            nextSequence++;
        }
    }

    private bool MatchesActiveSessionLocked(string projectId, string sessionId) =>
        _activeSession is not null &&
        string.Equals(_activeSession.ProjectId, projectId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(_activeSession.SessionId, sessionId, StringComparison.OrdinalIgnoreCase);

    private void ResetBrokerOrderingStateLocked(long generation)
    {
        _currentGeneration = generation;
        _acknowledgedSequence = null;
        _highestObservedBrokerSequence = null;
        _observedBrokerSequences.Clear();
        _appliedBrokerSequences.Clear();
        _appliedReplayResponseMessageIds.Clear();
        _gapDetected = false;
        _replayRequestPending = false;
    }

    private readonly record struct BrokerEnvelopeTrackingResult(
        bool ShouldProcess,
        bool ShouldAppend,
        bool GapJustDetected);

    private bool IsGapDetected()
    {
        lock (_stateSync)
        {
            return _gapDetected;
        }
    }

    private async Task RequestReplayAsync(
        ReplayRequestReason reason,
        bool operationGateHeld,
        CancellationToken cancellationToken)
    {
        if (!operationGateHeld)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var replayRequest = CreateReplayRequestEnvelope(reason);
            if (replayRequest is null)
            {
                return;
            }

            await SendEnvelopeCoreAsync(replayRequest, requireConnectedStatus: true, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_stateSync)
            {
                _replayRequestPending = false;
            }

            throw;
        }
        finally
        {
            if (!operationGateHeld)
            {
                _operationGate.Release();
            }
        }
    }

    private void QueueReplayRequest(ReplayRequestReason reason, CancellationToken cancellationToken)
    {
        _ = RunReplayRequestAsync(reason, cancellationToken);
    }

    private async Task RunReplayRequestAsync(ReplayRequestReason reason, CancellationToken cancellationToken)
    {
        try
        {
            await RequestReplayAsync(reason, operationGateHeld: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PublishReceiveFault($"The live session stream could not request replay: {ex.Message}");
        }
    }

    private MessageEnvelope<ReplayRequestPayload>? CreateReplayRequestEnvelope(ReplayRequestReason reason)
    {
        lock (_stateSync)
        {
            if (_activeSession is null)
            {
                throw new InvalidOperationException("An active session is required before replay can be requested.");
            }

            if (reason == ReplayRequestReason.GapDetected && _replayRequestPending)
            {
                return null;
            }

            _replayRequestPending = true;
            return new MessageEnvelope<ReplayRequestPayload>
            {
                ProjectId = _activeSession.ProjectId,
                SessionId = _activeSession.SessionId,
                Generation = _currentGeneration,
                MessageType = SessionMessageType.ReplayRequest,
                Direction = MessageDirection.ClientToBroker,
                AcknowledgedSequence = _acknowledgedSequence,
                TimestampUtc = _utcNow(),
                MessageId = $"replay-{_activeSession.SessionId}-{Guid.NewGuid():n}",
                CorrelationId = $"mobile-session:{_activeSession.SessionId}",
                Payload = new ReplayRequestPayload
                {
                    FromSequenceInclusive = (_acknowledgedSequence ?? 0) + 1,
                    Reason = reason
                }
            };
        }
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

    private async Task SendEnvelopeCoreAsync<TPayload>(
        MessageEnvelope<TPayload> envelope,
        bool requireConnectedStatus,
        CancellationToken cancellationToken)
    {
        var (session, socket) = GetTransportForSend(requireConnectedStatus);
        EnsureEnvelopeTargetsSession(envelope, session);

        if (envelope.Direction != MessageDirection.ClientToBroker)
        {
            throw new InvalidOperationException("Only client-to-broker envelopes can be sent from the mobile transport.");
        }

        var ackId = Interlocked.Increment(ref _nextAckId);
        await SendCommandExpectAckAsync(
                socket,
                new WebPubSubSendEventCommand
                {
                    Event = ResolveUpstreamEventName(envelope.MessageType),
                    DataType = "json",
                    Data = JsonSerializer.SerializeToElement(envelope, SessionMessageSerializer.DefaultOptions),
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
                ObservedAtUtc = _utcNow(),
                Summary = $"Sent {envelope.MessageType} for session '{session.SessionId}'."
            });
    }

    private async Task CleanupTransportAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? refreshLoopCts;
        IWebPubSubSocket? socket;
        CancellationTokenSource? receiveLoopCts;
        Task? receiveLoopTask;

        lock (_stateSync)
        {
            refreshLoopCts = _refreshLoopCts;
            _refreshLoopTask = null;
            _refreshLoopCts = null;
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
        refreshLoopCts?.Cancel();
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

        refreshLoopCts?.Dispose();
        receiveLoopCts?.Dispose();
    }

    private (SessionDescriptor Session, IWebPubSubSocket Socket) GetTransportForSend(bool requireConnectedStatus)
    {
        lock (_stateSync)
        {
            if (_activeSession is null || _negotiation is null || _socket is null ||
                (requireConnectedStatus && _currentStatus.State != MessageConnectionState.Connected))
            {
                throw new InvalidOperationException("Live messaging is not connected. Use Retry live transport to reconnect before sending.");
            }

            return (_activeSession, _socket);
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
            return MatchesActiveSessionLocked(session.ProjectId, session.SessionId);
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
            ResetBrokerOrderingStateLocked(SessionEnvelopeContract.InitialGeneration);
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

    private void ScheduleTokenRefresh(PubSubNegotiateResponse negotiation)
    {
        if (negotiation.RefreshAtUtc == default)
        {
            return;
        }

        var delay = CalculateRefreshDelay(negotiation.RefreshAtUtc);
        var refreshLoopCts = new CancellationTokenSource();
        var refreshLoopTask = Task.Run(
            () => WaitForTokenRefreshAsync(delay, refreshLoopCts.Token),
            CancellationToken.None);

        lock (_stateSync)
        {
            _refreshLoopCts = refreshLoopCts;
            _refreshLoopTask = refreshLoopTask;
        }
    }

    private async Task WaitForTokenRefreshAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await _delayAsync(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await RefreshTokenAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshTokenAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = GetActiveSession();
            if (session is null || CurrentStatus.State != MessageConnectionState.Connected)
            {
                return;
            }

            try
            {
                var negotiation = await _negotiationClient.NegotiateAsync(session, CancellationToken.None).ConfigureAwait(false);
                await ConnectCoreAsync(
                        isReconnect: true,
                        reconnectAttempt: CurrentStatus.ReconnectAttempt,
                        CancellationToken.None,
                        negotiationOverride: negotiation,
                        publishTransitionStatus: false,
                        failureReasonPrefix: "Token refresh failed. Retry the live transport after confirming the broker negotiate endpoint is reachable.")
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await CleanupTransportAsync(CancellationToken.None).ConfigureAwait(false);
                PublishStatus(
                    CreateFaultedStatus(
                        session,
                        CurrentStatus.ReconnectAttempt,
                        ComposeFailureReason(
                            "Token refresh failed. Retry the live transport after confirming the broker negotiate endpoint is reachable.",
                            ex.Message)));
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private TimeSpan CalculateRefreshDelay(DateTimeOffset refreshAtUtc)
    {
        var remaining = refreshAtUtc - _utcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var fiveMinuteDelay = remaining - TokenRefreshLeadTime;
        if (fiveMinuteDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var seventyFivePercentDelay = TimeSpan.FromTicks((long)(remaining.Ticks * 0.75d));
        return fiveMinuteDelay < seventyFivePercentDelay
            ? fiveMinuteDelay
            : seventyFivePercentDelay;
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

    private bool TryRejectStaleBrokerEnvelope(MessageEnvelope<JsonElement> envelope, string sourceDescription)
    {
        long currentGeneration;
        lock (_stateSync)
        {
            currentGeneration = _currentGeneration;
        }

        if (envelope.Generation >= currentGeneration)
        {
            return false;
        }

        AppendTraffic(
            new MessageEnvelopeTraffic
            {
                Direction = MessageTrafficDirection.Incoming,
                Envelope = ToJsonEnvelope(envelope),
                ObservedAtUtc = _utcNow(),
                Summary =
                    $"Rejected {sourceDescription} for session '{envelope.SessionId}' because generation {envelope.Generation} is older than active generation {currentGeneration}."
            });

        PublishStatus(CreateStaleGenerationWarningStatus(envelope.SessionId, sourceDescription, envelope.Generation, currentGeneration));
        return true;
    }

    private void PublishInvalidReplayGenerationBoundary(
        MessageEnvelope<JsonElement> envelope,
        long actualGeneration,
        long expectedGeneration,
        string sourceDescription)
    {
        AppendTraffic(
            new MessageEnvelopeTraffic
            {
                Direction = MessageTrafficDirection.Incoming,
                Envelope = ToJsonEnvelope(envelope),
                ObservedAtUtc = _utcNow(),
                Summary =
                    $"Rejected {sourceDescription} for session '{envelope.SessionId}' because generation {actualGeneration} did not match replay generation {expectedGeneration}."
            });

        PublishStatus(CreateInvalidReplayGenerationStatus(envelope.SessionId, sourceDescription, actualGeneration, expectedGeneration));
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

    private MessageConnectionStatus? TryCreateConnectedStatus()
    {
        lock (_stateSync)
        {
            if (_activeSession is null || _negotiation is null || _currentStatus.State == MessageConnectionState.Disconnected)
            {
                return null;
            }

            return new MessageConnectionStatus
            {
                State = MessageConnectionState.Connected,
                Summary = _currentStatus.ReconnectAttempt > 0
                    ? $"Live messaging reconnected for session '{_activeSession.SessionId}' on hub '{_negotiation.Hub}'."
                    : $"Live messaging connected for session '{_activeSession.SessionId}' on hub '{_negotiation.Hub}'.",
                Hub = _negotiation.Hub,
                SupportsLiveSessionStream = true,
                ProjectId = _activeSession.ProjectId,
                SessionId = _activeSession.SessionId,
                SessionGroup = _negotiation.SessionGroup,
                ConnectionId = _connectionId,
                ConnectedAtUtc = _currentStatus.ConnectedAtUtc,
                RefreshAtUtc = _negotiation.RefreshAtUtc,
                ReconnectAttempt = _currentStatus.ReconnectAttempt
            };
        }
    }

    private MessageConnectionStatus CreateReplayGapWarningStatus(string sessionId, ReplayResponsePayload payload)
    {
        var windowSummary = payload.AvailableFromSequence is long availableFrom && payload.AvailableToSequence is long availableTo
            ? $" Available replay window: {availableFrom}-{availableTo}."
            : string.Empty;
        var failureReason =
            $"Replay could not fully recover session '{sessionId}' because the broker reported a replay gap.{windowSummary} Reconnect or restart the session before trusting transcript continuity.";

        var connectedStatus = TryCreateConnectedStatus();
        if (connectedStatus is null)
        {
            var session = GetActiveSession()
                ?? throw new InvalidOperationException("An active session is required before replay recovery warnings can be published.");
            return CreateFaultedStatus(session, CurrentStatus.ReconnectAttempt, failureReason);
        }

        return connectedStatus with
        {
            State = MessageConnectionState.Faulted,
            Summary = $"Live messaging replay warning for session '{sessionId}'. {failureReason}",
            FailureReason = failureReason
        };
    }

    private MessageConnectionStatus CreateStaleGenerationWarningStatus(
        string sessionId,
        string sourceDescription,
        long rejectedGeneration,
        long currentGeneration)
    {
        var failureReason =
            $"The mobile client rejected {sourceDescription} from stale generation {rejectedGeneration} because session '{sessionId}' is already on generation {currentGeneration}. Reconnect or restart the session before trusting transcript continuity.";

        return CreateGenerationContinuityWarningStatus(
            sessionId,
            $"Live messaging rejected stale generation {rejectedGeneration} traffic for session '{sessionId}'. {failureReason}",
            failureReason);
    }

    private MessageConnectionStatus CreateInvalidReplayGenerationStatus(
        string sessionId,
        string sourceDescription,
        long actualGeneration,
        long expectedGeneration)
    {
        var failureReason =
            $"The {sourceDescription} for session '{sessionId}' reported generation {actualGeneration}, but replay recovery expected generation {expectedGeneration}. Reconnect or restart the session before trusting transcript continuity.";

        return CreateGenerationContinuityWarningStatus(
            sessionId,
            $"Live messaging rejected replay recovery data for session '{sessionId}'. {failureReason}",
            failureReason);
    }

    private MessageConnectionStatus CreateGenerationContinuityWarningStatus(
        string sessionId,
        string summary,
        string failureReason)
    {
        var connectedStatus = TryCreateConnectedStatus();
        if (connectedStatus is null)
        {
            var session = GetActiveSession()
                ?? throw new InvalidOperationException($"Active session '{sessionId}' is required before generation continuity warnings can be published.");
            return CreateFaultedStatus(session, CurrentStatus.ReconnectAttempt, failureReason);
        }

        return connectedStatus with
        {
            State = MessageConnectionState.Faulted,
            Summary = summary,
            FailureReason = failureReason
        };
    }

    private static string ResolveUpstreamEventName(SessionMessageType messageType) =>
        messageType switch
        {
            SessionMessageType.Input => SessionUpstreamEventNames.Input,
            SessionMessageType.ReplayRequest => SessionUpstreamEventNames.ReplayRequest,
            _ => throw new ArgumentOutOfRangeException(
                nameof(messageType),
                messageType,
                "No Azure Web PubSub upstream event is defined for this session message type.")
        };

    private static string ComposeFailureReason(string? prefix, string detail) =>
        string.IsNullOrWhiteSpace(prefix)
            ? detail
            : $"{prefix} {detail}";

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
            Payload = SecretRedactor.Redact(
                envelope.Payload is JsonElement jsonPayload
                    ? jsonPayload.Clone()
                    : JsonSerializer.SerializeToElement(envelope.Payload, SessionMessageSerializer.DefaultOptions))
        };

    private sealed record WebPubSubJoinGroupCommand
    {
        public string Type { get; init; } = "joinGroup";

        public string Group { get; init; } = string.Empty;

        public long AckId { get; init; }
    }

    private sealed record WebPubSubSendEventCommand
    {
        public string Type { get; init; } = "event";

        public string Event { get; init; } = string.Empty;

        public string DataType { get; init; } = "json";

        public JsonElement Data { get; init; }

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
