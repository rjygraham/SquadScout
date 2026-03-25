using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Sessions;

public static class SessionDiagnosticsDefaults
{
    public const int ExportVersion = 1;
    public const int RecentEnvelopeCapacity = 32;
    public const int RecentEventCapacity = 64;
    public const int PayloadPreviewCharacterLimit = 512;
}

public sealed record SessionReplayBufferTelemetry
{
    public int Capacity { get; init; }

    public int Count { get; init; }

    public long? AvailableFromSequence { get; init; }

    public long? AvailableToSequence { get; init; }
}

public sealed record SessionTelemetryEnvelope
{
    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public SessionMessageType MessageType { get; init; }

    public MessageDirection Direction { get; init; }

    public long Generation { get; init; } = SessionEnvelopeContract.InitialGeneration;

    public long? Sequence { get; init; }

    public long? ClientSequence { get; init; }

    public long? AcknowledgedSequence { get; init; }

    public string MessageId { get; init; } = string.Empty;

    public string CorrelationId { get; init; } = string.Empty;

    public string? CausationId { get; init; }

    public string PayloadType { get; init; } = string.Empty;

    public string PayloadPreview { get; init; } = string.Empty;
}

public enum SessionTelemetryEventType
{
    SessionStarted,
    ClientEnvelopeValidated,
    ClientEnvelopeForwardFailed,
    ReplayResponseCreated,
    GenerationReset
}

public sealed record SessionTelemetryEvent
{
    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public SessionTelemetryEventType EventType { get; init; }

    public string Summary { get; init; } = string.Empty;

    public SessionMessageType? MessageType { get; init; }

    public long Generation { get; init; } = SessionEnvelopeContract.InitialGeneration;

    public long? Sequence { get; init; }

    public long? ClientSequence { get; init; }

    public long? ExpectedClientSequence { get; init; }

    public long? LastAcceptedClientSequence { get; init; }

    public long? AcknowledgedSequence { get; init; }

    public string? MessageId { get; init; }

    public string? CorrelationId { get; init; }

    public string? CausationId { get; init; }

    public SequenceValidationStatus? ValidationStatus { get; init; }

    public bool? GapDetected { get; init; }

    public long? RequestedFromSequence { get; init; }

    public long? RequestedToSequence { get; init; }

    public long? AvailableFromSequence { get; init; }

    public long? AvailableToSequence { get; init; }

    public bool? HasMore { get; init; }

    public bool? IsComplete { get; init; }

    public string? Reason { get; init; }
}

public sealed record SessionTelemetrySnapshot
{
    public int ExportVersion { get; init; } = SessionDiagnosticsDefaults.ExportVersion;

    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public SessionDescriptor Session { get; init; } = new();

    public SessionSequencingSnapshot Sequencing { get; init; } =
        new(SessionEnvelopeContract.InitialGeneration, LastBrokerSequence: 0, LastClientSequence: null, AcknowledgedSequence: null);

    public SessionReplayBufferTelemetry ReplayBuffer { get; init; } = new();

    public IReadOnlyList<SessionTelemetryEnvelope> RecentEnvelopes { get; init; } = Array.Empty<SessionTelemetryEnvelope>();

    public IReadOnlyList<SessionTelemetryEvent> RecentEvents { get; init; } = Array.Empty<SessionTelemetryEvent>();
}
