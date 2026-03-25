using SquadScout.App.Services;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.ViewModels;

public sealed class SessionTranscriptController
{
    private static readonly TimeSpan GroupWindow = TimeSpan.FromMinutes(5);

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
        string draft)
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

        AppendMessage(
            TranscriptMessageSpeaker.LocalUser,
            string.IsNullOrWhiteSpace(authorDisplayName) ? "You" : authorDisplayName.Trim(),
            normalizedDraft,
            isError: false,
            isSystem: false);

        var statusMessage = connectionStatus.SupportsLiveSessionStream && connectionStatus.State == MessageConnectionState.Connected
            ? "Sent the message to the live session stream."
            : snapshot.Source == SessionActivationSource.DevelopmentFallback
                ? "Captured the message in the offline transcript preview."
                : "Captured the message in the native transcript preview while live streaming lands in #11.";

        return new TranscriptSendResult(
            Success: true,
            StatusMessage: statusMessage,
            ErrorMessage: string.Empty,
            ViewState: BuildViewState(snapshot, connectionStatus));
    }

    public SessionTranscriptViewState Sync(
        ActiveSessionSnapshot snapshot,
        MessageConnectionStatus connectionStatus)
    {
        if (!snapshot.HasActiveSession || snapshot.Session is null)
        {
            _sessionKey = null;
            _trackedState = null;
            _messages.Clear();
            return BuildViewState(snapshot, connectionStatus);
        }

        var nextSessionKey = CreateSessionKey(snapshot);
        if (!string.Equals(_sessionKey, nextSessionKey, StringComparison.Ordinal))
        {
            _messages.Clear();
            _sessionKey = nextSessionKey;
            _trackedState = snapshot.Session.State;
            return BuildViewState(snapshot, connectionStatus);
        }

        if (_trackedState != snapshot.Session.State)
        {
            AppendLifecycleMessage(snapshot.Session.State);
            _trackedState = snapshot.Session.State;
        }

        return BuildViewState(snapshot, connectionStatus);
    }

    private void AppendLifecycleMessage(SessionState sessionState)
    {
        var text = sessionState switch
        {
            SessionState.Pending => "The broker created the session. New transcript activity will appear here when it starts flowing.",
            SessionState.Running => "The session is running. Incoming replies will stack here in chat form.",
            SessionState.Stopped => "The session has ended. Review the transcript here, but sending is disabled.",
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
                "This screen is wired for native chat UX now; PubSub-backed live delivery lands in #11.",
                TranscriptBannerSeverity.Info));
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
            return "Send a message to preview the chat timeline while live PubSub delivery lands in #11.";
        }

        if (snapshot.Session?.State == SessionState.Pending)
        {
            return "The broker is still starting this session. Messaging unlocks once the live session is running.";
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

    private void EnsureSession(ActiveSessionSnapshot snapshot)
    {
        var nextSessionKey = CreateSessionKey(snapshot);
        if (string.Equals(_sessionKey, nextSessionKey, StringComparison.Ordinal))
        {
            return;
        }

        _messages.Clear();
        _sessionKey = nextSessionKey;
        _trackedState = snapshot.Session!.State;
    }
}
