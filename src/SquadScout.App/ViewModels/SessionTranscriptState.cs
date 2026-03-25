namespace SquadScout.App.ViewModels;

public enum TranscriptBannerSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public enum TranscriptMessageSpeaker
{
    LocalUser,
    RemoteAgent,
    System
}

public sealed record TranscriptBannerState(
    string Title,
    string Message,
    TranscriptBannerSeverity Severity);

public sealed record TranscriptMessageState(
    string Id,
    TranscriptMessageSpeaker Speaker,
    string SpeakerLabel,
    string Text,
    DateTimeOffset Timestamp,
    string TimestampLabel,
    bool IsOutgoing,
    bool IsSystem,
    bool IsError,
    bool ShowSpeakerLabel,
    bool ShowTimestamp,
    bool UseCompactTopSpacing);

public sealed record SessionTranscriptViewState(
    IReadOnlyList<TranscriptBannerState> Banners,
    IReadOnlyList<TranscriptMessageState> Messages,
    bool CanCompose,
    string ComposerPlaceholder,
    string EmptyTitle,
    string EmptyDescription);

public sealed record TranscriptSendResult(
    bool Success,
    string StatusMessage,
    string ErrorMessage,
    SessionTranscriptViewState ViewState);
