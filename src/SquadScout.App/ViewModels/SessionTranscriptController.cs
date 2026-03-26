using System.Text.Json;
using SquadScout.App.Services;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.ViewModels;

public sealed class SessionTranscriptController
{
    private static readonly TimeSpan GroupWindow = TimeSpan.FromMinutes(5);

    private readonly HashSet<string> _appliedTrafficKeys = new(StringComparer.Ordinal);
    private readonly List<TranscriptMessageState> _messages = [];
    private readonly Func<DateTimeOffset> _utcNow;

    private string? _sessionKey;
    private SessionState? _trackedState;

    public SessionTranscriptController(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public TranscriptSendResult SendDraft(
        ActiveSessionSnapshot snapshot,
        MessageConnectionStatus connectionStatus,
        string authorDisplayName,
        string draft,
        bool appendDraftMessage = true)
    {
        if (!snapshot.HasActiveSession || snapshot.Session is null)
        {
            return new TranscriptSendResult(
                Success: false,
                StatusMessage: string.Empty,
                ErrorMessage: "Start or resume a session before sending a message.",
                ViewState: BuildViewState(snapshot, connectionStatus));
        }

        if (snapshot.Session.State == SessionState.Stopped)
        {
            return new TranscriptSendResult(
                Success: false,
                StatusMessage: string.Empty,
                ErrorMessage: "This session has already stopped, so sending is disabled.",
                ViewState: BuildViewState(snapshot, connectionStatus));
        }

        var transportBlockedReason = GetComposeBlockedReason(snapshot, connectionStatus);
        if (!string.IsNullOrWhiteSpace(transportBlockedReason))
        {
            return new TranscriptSendResult(
                Success: false,
                StatusMessage: string.Empty,
                ErrorMessage: transportBlockedReason,
                ViewState: BuildViewState(snapshot, connectionStatus));
        }

        var normalizedDraft = draft?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedDraft))
        {
            return new TranscriptSendResult(
                Success: false,
                StatusMessage: string.Empty,
                ErrorMessage: "Type a message before sending it.",
                ViewState: BuildViewState(snapshot, connectionStatus));
        }

        EnsureSession(snapshot);
        if (appendDraftMessage)
        {
            AppendMessage(
                TranscriptMessageSpeaker.LocalUser,
                string.IsNullOrWhiteSpace(authorDisplayName) ? "You" : authorDisplayName.Trim(),
                normalizedDraft,
                isError: false,
                isSystem: false);
        }

        var statusMessage = connectionStatus.SupportsLiveSessionStream && connectionStatus.State == MessageConnectionState.Connected
            ? "Sent the message to the live session stream."
            : snapshot.Source == SessionActivationSource.DevelopmentFallback
                ? "Captured the message in the offline transcript preview."
                : "Captured the message in the native transcript preview while live delivery is unavailable.";

        return new TranscriptSendResult(
            Success: true,
            StatusMessage: statusMessage,
            ErrorMessage: string.Empty,
            ViewState: BuildViewState(snapshot, connectionStatus));
    }

    public SessionTranscriptViewState RestoreFromTraffic(
        ActiveSessionSnapshot snapshot,
        MessageConnectionStatus connectionStatus,
        IReadOnlyList<MessageEnvelopeTraffic> trafficHistory)
    {
        if (!snapshot.HasActiveSession || snapshot.Session is null)
        {
            ResetSessionState();
            return BuildViewState(snapshot, connectionStatus);
        }

        EnsureSession(snapshot, clearMessages: true);
        foreach (var traffic in trafficHistory)
        {
            ApplyTraffic(snapshot, traffic);
        }

        return BuildViewState(snapshot, connectionStatus);
    }

    public SessionTranscriptViewState ObserveTraffic(
        ActiveSessionSnapshot snapshot,
        MessageConnectionStatus connectionStatus,
        MessageEnvelopeTraffic traffic)
    {
        if (!snapshot.HasActiveSession || snapshot.Session is null)
        {
            return BuildViewState(snapshot, connectionStatus);
        }

        EnsureSession(snapshot);
        ApplyTraffic(snapshot, traffic);
        return BuildViewState(snapshot, connectionStatus);
    }

    public SessionTranscriptViewState Sync(
        ActiveSessionSnapshot snapshot,
        MessageConnectionStatus connectionStatus)
    {
        if (!snapshot.HasActiveSession || snapshot.Session is null)
        {
            ResetSessionState();
            return BuildViewState(snapshot, connectionStatus);
        }

        var nextSessionKey = CreateSessionKey(snapshot);
        if (!string.Equals(_sessionKey, nextSessionKey, StringComparison.Ordinal))
        {
            EnsureSession(snapshot, clearMessages: true);
            return BuildViewState(snapshot, connectionStatus);
        }

        if (_trackedState != snapshot.Session.State)
        {
            AppendLifecycleMessage(snapshot.Session.State);
            _trackedState = snapshot.Session.State;
        }

        return BuildViewState(snapshot, connectionStatus);
    }

    private void ApplyTraffic(ActiveSessionSnapshot snapshot, MessageEnvelopeTraffic traffic)
    {
        if (!MatchesSession(snapshot, traffic.Envelope))
        {
            return;
        }

        var trafficKey = CreateTrafficKey(traffic);
        if (!string.IsNullOrWhiteSpace(trafficKey) && !_appliedTrafficKeys.Add(trafficKey))
        {
            return;
        }

        switch (traffic.Direction, traffic.Envelope.MessageType)
        {
            case (MessageTrafficDirection.Outgoing, SessionMessageType.Input):
                TryAppendOutgoingInput(traffic.Envelope);
                break;

            case (MessageTrafficDirection.Outgoing, SessionMessageType.ReplayRequest):
                TryAppendReplayRequest(traffic.Envelope);
                break;

            case (MessageTrafficDirection.Incoming, SessionMessageType.Output):
                TryAppendIncomingOutput(traffic.Envelope);
                break;

            case (MessageTrafficDirection.Incoming, SessionMessageType.SessionLifecycle):
                TryAppendLifecycleEnvelope(traffic.Envelope);
                break;

            case (MessageTrafficDirection.Incoming, SessionMessageType.ReplayResponse):
                TryAppendReplayResponse(traffic.Envelope);
                break;
        }
    }

    private void TryAppendOutgoingInput(MessageEnvelope<JsonElement> envelope)
    {
        var payload = envelope.Payload.Deserialize<InputChunkPayload>(SessionMessageSerializer.DefaultOptions);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Content))
        {
            return;
        }

        AppendMessage(
            TranscriptMessageSpeaker.LocalUser,
            "You",
            payload.Content.Trim(),
            isError: false,
            isSystem: false);
    }

    private void TryAppendIncomingOutput(MessageEnvelope<JsonElement> envelope)
    {
        var payload = envelope.Payload.Deserialize<OutputChunkPayload>(SessionMessageSerializer.DefaultOptions);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Content))
        {
            return;
        }

        AppendMessage(
            TranscriptMessageSpeaker.RemoteAgent,
            "SquadScout",
            payload.Content,
            payload.IsError,
            isSystem: false);
    }

    private void TryAppendLifecycleEnvelope(MessageEnvelope<JsonElement> envelope)
    {
        var payload = envelope.Payload.Deserialize<SessionLifecyclePayload>(SessionMessageSerializer.DefaultOptions);
        if (payload is null)
        {
            return;
        }

        AppendLifecycleMessage(payload.State, payload.Reason);
        _trackedState = payload.State;
    }

    private void TryAppendReplayRequest(MessageEnvelope<JsonElement> envelope)
    {
        var payload = envelope.Payload.Deserialize<ReplayRequestPayload>(SessionMessageSerializer.DefaultOptions);
        if (payload is null)
        {
            return;
        }

        var reasonText = payload.Reason switch
        {
            ReplayRequestReason.ClientRecovery => "Resuming this session from the device cache.",
            ReplayRequestReason.ReconnectResume => "Reconnected to the broker.",
            _ => "Transcript continuity was interrupted."
        };

        AppendMessage(
            TranscriptMessageSpeaker.System,
            "SquadScout",
            $"{reasonText} Recovering transcript messages from sequence {payload.FromSequenceInclusive}.",
            isError: false,
            isSystem: true);
    }

    private void TryAppendReplayResponse(MessageEnvelope<JsonElement> envelope)
    {
        var payload = envelope.Payload.Deserialize<ReplayResponsePayload>(SessionMessageSerializer.DefaultOptions);
        if (payload is null)
        {
            return;
        }

        if (payload.GapDetected)
        {
            var windowText = payload.AvailableFromSequence is long availableFrom && payload.AvailableToSequence is long availableTo
                ? $" Available replay window: {availableFrom}-{availableTo}."
                : string.Empty;

            AppendMessage(
                TranscriptMessageSpeaker.System,
                "SquadScout",
                $"Replay could not fully recover the transcript. Some context is no longer available from the broker.{windowText}",
                isError: true,
                isSystem: true);
            return;
        }

        if (payload.HasMore)
        {
            AppendMessage(
                TranscriptMessageSpeaker.System,
                "SquadScout",
                $"Recovered {payload.Messages.Count} transcript message(s). Loading more from the broker…",
                isError: false,
                isSystem: true);
            return;
        }

        if (payload.Messages.Count > 0)
        {
            AppendMessage(
                TranscriptMessageSpeaker.System,
                "SquadScout",
                $"Recovered {payload.Messages.Count} transcript message(s). Live continuity is restored.",
                isError: false,
                isSystem: true);
            return;
        }

        AppendMessage(
            TranscriptMessageSpeaker.System,
            "SquadScout",
            "The session is current. No transcript replay was needed.",
            isError: false,
            isSystem: true);
    }

    private void AppendLifecycleMessage(SessionState sessionState, string? reason = null)
    {
        var text = sessionState switch
        {
            SessionState.Pending => "The broker created the session. New transcript activity will appear here when it starts flowing.",
            SessionState.Running => "The session is running. Incoming replies will stack here in chat form.",
            SessionState.Stopped when string.IsNullOrWhiteSpace(reason) =>
                "The session has ended. Review the transcript here, but sending is disabled.",
            SessionState.Stopped => $"The session has ended. {reason}".Trim(),
            _ => "The session state changed."
        };

        AppendMessage(
            TranscriptMessageSpeaker.System,
            "SquadScout",
            text,
            isError: false,
            isSystem: true);
    }

    private void AppendMessage(
        TranscriptMessageSpeaker speaker,
        string speakerLabel,
        string text,
        bool isError,
        bool isSystem)
    {
        var occurredAt = _utcNow().ToLocalTime();
        var previousMessage = _messages.Count == 0 ? null : _messages[^1];
        var useCompactTopSpacing = previousMessage is not null && CanGroup(previousMessage, speaker, isError, isSystem, occurredAt);

        if (useCompactTopSpacing && previousMessage is not null)
        {
            _messages[^1] = previousMessage with { ShowTimestamp = false };
        }

        _messages.Add(new TranscriptMessageState(
            Id: Guid.NewGuid().ToString("N"),
            Speaker: speaker,
            SpeakerLabel: speakerLabel,
            Text: text,
            Timestamp: occurredAt,
            TimestampLabel: occurredAt.ToString("t"),
            IsOutgoing: speaker == TranscriptMessageSpeaker.LocalUser,
            IsSystem: isSystem,
            IsError: isError,
            ShowSpeakerLabel: !isSystem && !useCompactTopSpacing,
            ShowTimestamp: true,
            UseCompactTopSpacing: useCompactTopSpacing));
    }

    private SessionTranscriptViewState BuildViewState(
        ActiveSessionSnapshot snapshot,
        MessageConnectionStatus connectionStatus)
    {
        var canCompose = string.IsNullOrWhiteSpace(GetComposeBlockedReason(snapshot, connectionStatus));

        return new SessionTranscriptViewState(
            Banners: BuildBanners(snapshot, connectionStatus),
            Messages: _messages.ToArray(),
            CanCompose: canCompose,
            ComposerPlaceholder: BuildComposerPlaceholder(snapshot, connectionStatus, canCompose),
            EmptyTitle: BuildEmptyTitle(snapshot),
            EmptyDescription: BuildEmptyDescription(snapshot, connectionStatus));
    }

    private static IReadOnlyList<TranscriptBannerState> BuildBanners(
        ActiveSessionSnapshot snapshot,
        MessageConnectionStatus connectionStatus)
    {
        if (!snapshot.HasActiveSession || snapshot.Session is null)
        {
            return [];
        }

        var banners = new List<TranscriptBannerState>
        {
            snapshot.Session.State switch
            {
                SessionState.Pending => new TranscriptBannerState(
                    "Session pending",
                    "The session exists, but live transcript traffic has not started yet.",
                    TranscriptBannerSeverity.Warning),
                SessionState.Running => new TranscriptBannerState(
                    "Session active",
                    "Transcript bubbles stay native and chat-like instead of terminal-emulated.",
                    TranscriptBannerSeverity.Success),
                SessionState.Stopped => new TranscriptBannerState(
                    "Session stopped",
                    "You can keep reading the transcript, but the composer is now disabled.",
                    TranscriptBannerSeverity.Info),
                _ => new TranscriptBannerState(
                    "Session update",
                    "The session state changed.",
                    TranscriptBannerSeverity.Info)
            }
        };

        if (!connectionStatus.SupportsLiveSessionStream)
        {
            banners.Add(new TranscriptBannerState(
                "Transcript preview",
                "This screen keeps the native chat UX even when live delivery is unavailable.",
                TranscriptBannerSeverity.Info));
        }
        else if (connectionStatus.IsReplayPending)
        {
            banners.Add(new TranscriptBannerState(
                connectionStatus.ReplayReason == ReplayRequestReason.ClientRecovery
                    ? "Resuming saved session"
                    : "Recovering transcript",
                connectionStatus.Summary,
                TranscriptBannerSeverity.Warning));
        }
        else if (connectionStatus.State != MessageConnectionState.Connected)
        {
            banners.Add(new TranscriptBannerState(
                connectionStatus.State switch
                {
                    MessageConnectionState.Ready => "Live transport staged",
                    MessageConnectionState.Connecting or MessageConnectionState.Reconnecting => "Reconnecting",
                    _ => "Live transport unavailable"
                },
                connectionStatus.Summary,
                connectionStatus.State == MessageConnectionState.Faulted
                    ? TranscriptBannerSeverity.Error
                    : TranscriptBannerSeverity.Warning));
        }

        if (connectionStatus.ReplayAvailableFromSequence is long availableFrom &&
            connectionStatus.ReplayAvailableToSequence is long availableTo)
        {
            banners.Add(new TranscriptBannerState(
                "Replay gap detected",
                $"The broker can only replay messages {availableFrom}-{availableTo}. Earlier context must be treated as missing.",
                TranscriptBannerSeverity.Error));
        }

        if (snapshot.Source == SessionActivationSource.DevelopmentFallback)
        {
            banners.Add(new TranscriptBannerState(
                "Offline development mode",
                "Messages stay local to the device until the broker is reachable.",
                TranscriptBannerSeverity.Warning));
        }

        return banners;
    }

    private static string BuildComposerPlaceholder(
        ActiveSessionSnapshot snapshot,
        MessageConnectionStatus connectionStatus,
        bool canCompose)
    {
        if (!snapshot.HasActiveSession)
        {
            return "Start a session to compose messages.";
        }

        if (!canCompose)
        {
            return GetComposeBlockedReason(snapshot, connectionStatus) ?? "This session is closed.";
        }

        if (!connectionStatus.SupportsLiveSessionStream)
        {
            return "Draft a message in the native transcript preview.";
        }

        return "Message the active session.";
    }

    private static string BuildEmptyDescription(
        ActiveSessionSnapshot snapshot,
        MessageConnectionStatus connectionStatus)
    {
        if (!snapshot.HasActiveSession)
        {
            return "Return to projects and start a session to open the transcript timeline.";
        }

        if (snapshot.Session?.State == SessionState.Stopped)
        {
            return "No transcript messages were captured before the session ended.";
        }

        if (!connectionStatus.SupportsLiveSessionStream)
        {
            return "Send a message to preview the chat timeline while live delivery is unavailable.";
        }

        if (snapshot.Session?.State == SessionState.Pending)
        {
            return "The broker is still starting this session. Messaging unlocks once the live session is running.";
        }

        if (connectionStatus.IsReplayPending)
        {
            return "Replaying recent messages from the broker before trusting transcript continuity.";
        }

        if (connectionStatus.State != MessageConnectionState.Connected)
        {
            return connectionStatus.Summary;
        }

        return "Incoming replies and your own messages will appear here in order.";
    }

    private static string BuildEmptyTitle(ActiveSessionSnapshot snapshot)
    {
        if (!snapshot.HasActiveSession)
        {
            return "No active session";
        }

        return snapshot.Session?.State == SessionState.Stopped
            ? "Session ended"
            : "Transcript ready";
    }

    private static bool CanGroup(
        TranscriptMessageState previousMessage,
        TranscriptMessageSpeaker speaker,
        bool isError,
        bool isSystem,
        DateTimeOffset occurredAt)
    {
        if (previousMessage.IsSystem || isSystem)
        {
            return false;
        }

        return previousMessage.Speaker == speaker &&
               previousMessage.IsError == isError &&
               occurredAt - previousMessage.Timestamp <= GroupWindow;
    }

    private static string CreateSessionKey(ActiveSessionSnapshot snapshot) =>
        $"{snapshot.Project!.ProjectId}:{snapshot.Session!.SessionId}";

    private static string? GetComposeBlockedReason(
        ActiveSessionSnapshot snapshot,
        MessageConnectionStatus connectionStatus)
    {
        if (!snapshot.HasActiveSession || snapshot.Session is null)
        {
            return "Start or resume a session before sending a message.";
        }

        if (snapshot.Session.State == SessionState.Stopped)
        {
            return "This session has already stopped, so sending is disabled.";
        }

        if (!connectionStatus.SupportsLiveSessionStream)
        {
            return null;
        }

        if (connectionStatus.IsReplayPending)
        {
            return "Live messaging is recovering transcript continuity. Wait for replay to finish before sending.";
        }

        if (snapshot.Session.State == SessionState.Pending)
        {
            return "Wait for the broker to start the session before sending messages.";
        }

        return connectionStatus.State switch
        {
            MessageConnectionState.Connected => null,
            MessageConnectionState.Ready => "Live messaging is staged for this session. Reconnect the live transport before sending.",
            MessageConnectionState.Connecting or MessageConnectionState.Reconnecting => "Live messaging is reconnecting. Wait for the session stream before sending.",
            MessageConnectionState.Faulted => connectionStatus.FailureReason is { Length: > 0 }
                ? $"Live messaging is unavailable. {connectionStatus.FailureReason}"
                : "Live messaging is unavailable. Reconnect the live transport before sending.",
            _ => "Live messaging is offline. Reconnect the live transport before sending."
        };
    }

    private void EnsureSession(ActiveSessionSnapshot snapshot, bool clearMessages = false)
    {
        var nextSessionKey = CreateSessionKey(snapshot);
        if (string.Equals(_sessionKey, nextSessionKey, StringComparison.Ordinal))
        {
            if (clearMessages)
            {
                _messages.Clear();
                _appliedTrafficKeys.Clear();
                _trackedState = snapshot.Session!.State;
            }

            return;
        }

        _messages.Clear();
        _appliedTrafficKeys.Clear();
        _sessionKey = nextSessionKey;
        _trackedState = snapshot.Session!.State;
    }

    private void ResetSessionState()
    {
        _sessionKey = null;
        _trackedState = null;
        _messages.Clear();
        _appliedTrafficKeys.Clear();
    }

    private static bool MatchesSession(ActiveSessionSnapshot snapshot, MessageEnvelope<JsonElement> envelope) =>
        snapshot.Project is not null &&
        snapshot.Session is not null &&
        string.Equals(snapshot.Project.ProjectId, envelope.ProjectId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(snapshot.Session.SessionId, envelope.SessionId, StringComparison.OrdinalIgnoreCase);

    private static string? CreateTrafficKey(MessageEnvelopeTraffic traffic)
    {
        var envelope = traffic.Envelope;
        if (!string.IsNullOrWhiteSpace(envelope.MessageId))
        {
            return $"{traffic.Direction}:{envelope.MessageType}:{envelope.MessageId}";
        }

        if (envelope.Direction == MessageDirection.BrokerToClient &&
            envelope.Sequence is long sequence)
        {
            return $"{traffic.Direction}:{envelope.MessageType}:{envelope.Generation}:{sequence}";
        }

        if (envelope.Direction == MessageDirection.ClientToBroker &&
            envelope.ClientSequence is long clientSequence)
        {
            return $"{traffic.Direction}:{envelope.MessageType}:{clientSequence}";
        }

        return null;
    }
}
