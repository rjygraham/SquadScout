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

    public async Task<MessageConnectionStatus> PrepareForSessionAsync(
        SessionDescriptor session,
        MessageConnectionResumeState? resumeState = null,
        CancellationToken cancellationToken = default)
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

            if (resumeState is not null)
            {
                RestoreBrokerOrderingStateLocked(resumeState);
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

            return await ConnectCoreAsync(
                    isReconnect: resumeState is not null,
                    reconnectAttempt: 0,
                    cancellationToken,
                    initialReplayReason: resumeState is not null ? ReplayRequestReason.ClientRecovery : null)
                .ConfigureAwait(false);
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
        string? failureReasonPrefix = null,
        ReplayRequestReason? initialReplayReason = null)
    {
        var session = GetActiveSession()
            ?? throw new InvalidOperationException("An active session is required before live messaging can connect.");

        await CleanupTransportAsync(cancellationToken).ConfigureAwait(false);

        if (publishTransitionStatus)
        {
            PublishStatus(
                CreateStatusWithOrderingState(new MessageConnectionStatus
                {
                    State = isReconnect ? MessageConnectionState.Reconnecting : MessageConnectionState.Connecting,
                    Summary = initialReplayReason == ReplayRequestReason.ClientRecovery
                        ? $"Restoring session '{session.SessionId}' from this device."
                        : isReconnect
                        ? $"Retrying live messaging for session '{session.SessionId}'."
                        : $"Connecting live messaging for session '{session.SessionId}'.",
                    Hub = _messagingOptions.Hub,
                    SupportsLiveSessionStream = true,
                    ProjectId = session.ProjectId,
                    SessionId = session.SessionId,
                    ReconnectAttempt = reconnectAttempt
                }));
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

            var status = CreateStatusWithOrderingState(new MessageConnectionStatus
            {
                State = MessageConnectionState.Connected,
                Summary = initialReplayReason == ReplayRequestReason.ClientRecovery
                    ? $"Resumed live messaging for session '{session.SessionId}' on hub '{negotiation.Hub}'."
                    : isReconnect
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
            });

            PublishStatus(status);
            ScheduleTokenRefresh(negotiation);
            if (initialReplayReason is not null || isReconnect)
            {
                await RequestReplayAsync(
                        initialReplayReason ?? ReplayRequestReason.ReconnectResume,
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

        var replayReason = CurrentStatus.ReplayReason ?? ReplayRequestReason.GapDetected;

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

        if (payload.HasMore)
        {
            var nextFromInclusive = payload.Messages.Count > 0 && payload.Messages[^1].Sequence is long nextFromExclusive
                ? nextFromExclusive + 1
                : GetNextReplayFromSequence();

            QueueReplayRequest(
                replayReason,
                cancellationToken,
                fromSequenceInclusive: nextFromInclusive,
                toSequenceInclusive: payload.AvailableToSequence,
                publishReplayStatus: !payload.GapDetected);
            return true;
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

    private void RestoreBrokerOrderingStateLocked(MessageConnectionResumeState resumeState)
    {
        ArgumentNullException.ThrowIfNull(resumeState);

        _currentGeneration = Math.Max(SessionEnvelopeContract.InitialGeneration, resumeState.Generation);
        _acknowledgedSequence = resumeState.AcknowledgedSequence is > 0 ? resumeState.AcknowledgedSequence : null;
        _highestObservedBrokerSequence = _acknowledgedSequence;
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

    private long GetNextReplayFromSequence()
    {
        lock (_stateSync)
        {
            return (_acknowledgedSequence ?? 0) + 1;
        }
    }

    private async Task RequestReplayAsync(
        ReplayRequestReason reason,
        bool operationGateHeld,
        CancellationToken cancellationToken,
        long? fromSequenceInclusive = null,
        long? toSequenceInclusive = null,
        bool publishReplayStatus = true)
    {
        if (!operationGateHeld)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var replayRequest = CreateReplayRequestEnvelope(reason, fromSequenceInclusive, toSequenceInclusive);
            if (replayRequest is null)
            {
                return;
            }

            if (publishReplayStatus)
            {
                PublishStatus(CreateReplayPendingStatus(reason, replayRequest.Payload.FromSequenceInclusive));
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

    private void QueueReplayRequest(
        ReplayRequestReason reason,
        CancellationToken cancellationToken,
        long? fromSequenceInclusive = null,
        long? toSequenceInclusive = null,
        bool publishReplayStatus = true)
    {
        _ = RunReplayRequestAsync(
            reason,
            cancellationToken,
            fromSequenceInclusive,
            toSequenceInclusive,
            publishReplayStatus);
    }

    private async Task RunReplayRequestAsync(
        ReplayRequestReason reason,
        CancellationToken cancellationToken,
        long? fromSequenceInclusive = null,
        long? toSequenceInclusive = null,
        bool publishReplayStatus = true)
    {
        try
        {
            await RequestReplayAsync(
                    reason,
                    operationGateHeld: false,
                    cancellationToken,
                    fromSequenceInclusive,
                    toSequenceInclusive,
                    publishReplayStatus)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PublishReceiveFault($"The live session stream could not request replay: {ex.Message}");
        }
    }

    private MessageEnvelope<ReplayRequestPayload>? CreateReplayRequestEnvelope(
        ReplayRequestReason reason,
        long? fromSequenceInclusive = null,
        long? toSequenceInclusive = null)
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
                    FromSequenceInclusive = fromSequenceInclusive ?? (_acknowledgedSequence ?? 0) + 1,
                    ToSequenceInclusive = toSequenceInclusive,
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

    private PubSubNegotiateResponse? GetNegotiation()
    {
        lock (_stateSync)
        {
            return _negotiation;
        }
    }

    private bool HasConnectedTransport()
    {
        lock (_stateSync)
        {
            return _socket is not null && _currentStatus.State == MessageConnectionState.Connected;
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
        var retryCount = Math.Max(0, _messagingOptions.TokenRefreshRetryCount);
        for (var attempt = 1; ; attempt++)
        {
            SessionDescriptor? session = null;
            PubSubNegotiateResponse? activeNegotiation = null;

            try
            {
                await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    session = GetActiveSession();
                    activeNegotiation = GetNegotiation();
                    if (session is null || activeNegotiation is null)
                    {
                        return;
                    }

                    if (attempt == 1 && CurrentStatus.State != MessageConnectionState.Connected)
                    {
                        return;
                    }

                    if (attempt > 1 && CurrentStatus.State == MessageConnectionState.Disconnected)
                    {
                        return;
                    }

                    var negotiation = await _negotiationClient.NegotiateAsync(session, cancellationToken).ConfigureAwait(false);
                    ValidateRefreshedNegotiation(session, activeNegotiation, negotiation);

                    var status = await ConnectCoreAsync(
                            isReconnect: true,
                            reconnectAttempt: CurrentStatus.ReconnectAttempt,
                            CancellationToken.None,
                            negotiationOverride: negotiation,
                            publishTransitionStatus: false,
                            failureReasonPrefix: "Token refresh failed. Retry the live transport after confirming the broker negotiate endpoint is reachable.")
                        .ConfigureAwait(false);

                    if (status.State == MessageConnectionState.Connected)
                    {
                        return;
                    }

                    throw new InvalidOperationException(status.FailureReason ?? status.Summary);
                }
                finally
                {
                    _operationGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (TokenRefreshAuthDriftException driftException)
            {
                session ??= GetActiveSession();
                if (session is null)
                {
                    return;
                }

                await FailTokenRefreshAsync(
                        session,
                        ComposeFailureReason(
                            "Token refresh detected authentication drift. Retry the live transport after confirming the active account still owns this session.",
                            driftException.Message))
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                session ??= GetActiveSession();
                activeNegotiation ??= GetNegotiation();
                if (session is null || activeNegotiation is null)
                {
                    return;
                }

                if (attempt > retryCount)
                {
                    await FailTokenRefreshAsync(
                            session,
                            ComposeFailureReason(
                                $"Token refresh failed after {attempt} attempt{(attempt == 1 ? string.Empty : "s")}. Retry the live transport after confirming the broker negotiate endpoint is reachable.",
                                ex.Message))
                        .ConfigureAwait(false);
                    return;
                }

                var retryDelay = CalculateRefreshRetryDelay(activeNegotiation.ExpiresAtUtc, attempt);
                PublishStatus(CreateTokenRefreshRetryStatus(session, activeNegotiation, attempt, retryDelay, ex.Message));

                try
                {
                    await _delayAsync(retryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private TimeSpan CalculateRefreshDelay(DateTimeOffset refreshAtUtc)
    {
        var remaining = refreshAtUtc - _utcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return remaining;
    }

    private TimeSpan CalculateRefreshRetryDelay(DateTimeOffset expiresAtUtc, int failedAttempt)
    {
        var baseDelaySeconds = Math.Max(1, _messagingOptions.TokenRefreshRetryBaseDelaySeconds);
        var maxDelaySeconds = Math.Max(baseDelaySeconds, _messagingOptions.TokenRefreshRetryMaxDelaySeconds);
        var multiplier = Math.Pow(2d, Math.Max(0, failedAttempt - 1));
        var proposedDelay = TimeSpan.FromSeconds(Math.Min(maxDelaySeconds, baseDelaySeconds * multiplier));
        if (expiresAtUtc == default)
        {
            return proposedDelay;
        }

        var remainingLifetime = expiresAtUtc - _utcNow();
        if (remainingLifetime <= TimeSpan.FromSeconds(1))
        {
            return TimeSpan.Zero;
        }

        var clampedDelay = remainingLifetime - TimeSpan.FromSeconds(1);
        return proposedDelay <= clampedDelay
            ? proposedDelay
            : clampedDelay;
    }

    private void ValidateRefreshedNegotiation(
        SessionDescriptor session,
        PubSubNegotiateResponse currentNegotiation,
        PubSubNegotiateResponse refreshedNegotiation)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(currentNegotiation);
        ArgumentNullException.ThrowIfNull(refreshedNegotiation);

        if (!string.Equals(refreshedNegotiation.ProjectId, session.ProjectId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(refreshedNegotiation.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new TokenRefreshAuthDriftException(
                $"The negotiate endpoint returned credentials for {refreshedNegotiation.ProjectId}/{refreshedNegotiation.SessionId} instead of {session.ProjectId}/{session.SessionId}.");
        }

        if (!string.Equals(refreshedNegotiation.SessionGroup, currentNegotiation.SessionGroup, StringComparison.OrdinalIgnoreCase))
        {
            throw new TokenRefreshAuthDriftException(
                $"The negotiate endpoint returned session group '{refreshedNegotiation.SessionGroup}' instead of '{currentNegotiation.SessionGroup}'.");
        }

        if (!string.Equals(refreshedNegotiation.UserId, currentNegotiation.UserId, StringComparison.Ordinal))
        {
            throw new TokenRefreshAuthDriftException(
                $"The negotiate endpoint returned user '{refreshedNegotiation.UserId}' instead of '{currentNegotiation.UserId}'.");
        }

        if (!string.Equals(refreshedNegotiation.Hub, currentNegotiation.Hub, StringComparison.OrdinalIgnoreCase))
        {
            throw new TokenRefreshAuthDriftException(
                $"The negotiate endpoint returned hub '{refreshedNegotiation.Hub}' instead of '{currentNegotiation.Hub}'.");
        }
    }

    private async Task FailTokenRefreshAsync(SessionDescriptor session, string failureReason)
    {
        await CleanupTransportAsync(CancellationToken.None).ConfigureAwait(false);
        PublishStatus(CreateFaultedStatus(session, CurrentStatus.ReconnectAttempt, failureReason));
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
        CreateStatusWithOrderingState(new MessageConnectionStatus
        {
            State = MessageConnectionState.Ready,
            Summary = $"Live messaging is staged for session '{session.SessionId}'. Use Retry live transport to connect when needed.",
            Hub = _messagingOptions.Hub,
            SupportsLiveSessionStream = true,
            ProjectId = session.ProjectId,
            SessionId = session.SessionId
        });

    private MessageConnectionStatus CreateDisconnectedStatus(string? summary = null) =>
        CreateStatusWithOrderingState(new MessageConnectionStatus
        {
            State = MessageConnectionState.Disconnected,
            Summary = summary ?? $"Live messaging is disconnected from hub '{_messagingOptions.Hub}'.",
            Hub = _messagingOptions.Hub,
            SupportsLiveSessionStream = true
        });

    private MessageConnectionStatus CreateFaultedStatus(
        SessionDescriptor session,
        int reconnectAttempt,
        string failureReason) =>
        CreateStatusWithOrderingState(new MessageConnectionStatus
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
        });

    private MessageConnectionStatus CreateTokenRefreshRetryStatus(
        SessionDescriptor session,
        PubSubNegotiateResponse negotiation,
        int refreshAttempt,
        TimeSpan retryDelay,
        string detail)
    {
        var transportStillConnected = HasConnectedTransport();
        var baseStatus = transportStillConnected
            ? TryCreateConnectedStatus() ?? CurrentStatus
            : CurrentStatus;
        var summary = transportStillConnected
            ? $"Refreshing live messaging for session '{session.SessionId}' hit a transient failure. Retrying in {FormatDelay(retryDelay)} while the current session stream stays connected."
            : $"Refreshing live messaging for session '{session.SessionId}' lost the active session stream. Rejoining in {FormatDelay(retryDelay)}.";

        return CreateStatusWithOrderingState(baseStatus with
        {
            State = transportStillConnected ? MessageConnectionState.Connected : MessageConnectionState.Reconnecting,
            Summary = summary,
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            SessionGroup = negotiation.SessionGroup,
            RefreshAtUtc = negotiation.RefreshAtUtc,
            RefreshAttempt = refreshAttempt,
            FailureReason = detail,
            ReplayAvailableFromSequence = null,
            ReplayAvailableToSequence = null
        });
    }

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
                ReconnectAttempt = _currentStatus.ReconnectAttempt,
                Generation = _currentGeneration,
                AcknowledgedSequence = _acknowledgedSequence
            };
        }
    }

    private MessageConnectionStatus CreateReplayPendingStatus(ReplayRequestReason reason, long fromSequenceInclusive)
    {
        var connectedStatus = TryCreateConnectedStatus();
        var session = GetActiveSession();
        var sessionId = session?.SessionId ?? CurrentStatus.SessionId ?? "the active session";
        var summary = reason switch
        {
            ReplayRequestReason.ClientRecovery =>
                $"Resuming session '{sessionId}' from this device. Recovering transcript continuity from sequence {fromSequenceInclusive}.",
            ReplayRequestReason.ReconnectResume =>
                $"Live messaging reconnected for session '{sessionId}'. Recovering transcript continuity from sequence {fromSequenceInclusive}.",
            _ =>
                $"A broker sequence gap was detected for session '{sessionId}'. Recovering messages from sequence {fromSequenceInclusive}."
        };

        if (connectedStatus is null && session is not null)
        {
            connectedStatus = CreateFaultedStatus(session, CurrentStatus.ReconnectAttempt, summary);
        }

        return (connectedStatus ?? CurrentStatus) with
        {
            Summary = summary,
            IsReplayPending = true,
            ReplayReason = reason,
            ReplayFromSequenceInclusive = fromSequenceInclusive,
            ReplayAvailableFromSequence = null,
            ReplayAvailableToSequence = null
        };
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
            FailureReason = failureReason,
            ReplayAvailableFromSequence = payload.AvailableFromSequence,
            ReplayAvailableToSequence = payload.AvailableToSequence
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
            FailureReason = failureReason,
            ReplayAvailableFromSequence = null,
            ReplayAvailableToSequence = null
        };
    }

    private MessageConnectionStatus CreateStatusWithOrderingState(MessageConnectionStatus status)
    {
        lock (_stateSync)
        {
            return status with
            {
                Generation = _currentGeneration,
                AcknowledgedSequence = _acknowledgedSequence
            };
        }
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

    private static string FormatDelay(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return "0 seconds";
        }

        if (delay.TotalMinutes >= 1d && delay.Seconds == 0)
        {
            return $"{Math.Max(1, (int)Math.Round(delay.TotalMinutes, MidpointRounding.AwayFromZero))} minute(s)";
        }

        return $"{Math.Max(0, (int)Math.Ceiling(delay.TotalSeconds))} second(s)";
    }

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

    private sealed class TokenRefreshAuthDriftException : InvalidOperationException
    {
        public TokenRefreshAuthDriftException(string message)
            : base(message)
        {
        }
    }

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
