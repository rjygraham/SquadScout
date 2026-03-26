using System.Text.Json;
using Orleans;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Orleans;

[GenerateSerializer]
public sealed class SessionGrainStartCommand
{
    [Id(0)]
    public string SessionId { get; set; } = string.Empty;

    [Id(1)]
    public string ProjectId { get; set; } = string.Empty;

    [Id(2)]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [Id(3)]
    public string? RequestedBy { get; set; }
}

[GenerateSerializer]
public sealed class SessionDescriptorRecord
{
    [Id(0)]
    public string SessionId { get; set; } = string.Empty;

    [Id(1)]
    public string ProjectId { get; set; } = string.Empty;

    [Id(2)]
    public SessionState State { get; set; } = SessionState.Pending;

    [Id(3)]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

[GenerateSerializer]
public sealed class BrokerEnvelopeCommandRecord
{
    [Id(0)]
    public SessionMessageType MessageType { get; set; }

    [Id(1)]
    public string MessageId { get; set; } = string.Empty;

    [Id(2)]
    public string CorrelationId { get; set; } = string.Empty;

    [Id(3)]
    public string? CausationId { get; set; }

    [Id(4)]
    public DateTimeOffset? TimestampUtc { get; set; }

    [Id(5)]
    public string PayloadJson { get; set; } = "null";
}

[GenerateSerializer]
public sealed class SessionEnvelopeRecord
{
    [Id(0)]
    public int ContractVersion { get; set; } = SessionEnvelopeContract.CurrentVersion;

    [Id(1)]
    public string ProjectId { get; set; } = string.Empty;

    [Id(2)]
    public string SessionId { get; set; } = string.Empty;

    [Id(3)]
    public long Generation { get; set; } = SessionEnvelopeContract.InitialGeneration;

    [Id(4)]
    public SessionMessageType MessageType { get; set; }

    [Id(5)]
    public MessageDirection Direction { get; set; }

    [Id(6)]
    public long? Sequence { get; set; }

    [Id(7)]
    public long? ClientSequence { get; set; }

    [Id(8)]
    public long? AcknowledgedSequence { get; set; }

    [Id(9)]
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    [Id(10)]
    public string MessageId { get; set; } = string.Empty;

    [Id(11)]
    public string CorrelationId { get; set; } = string.Empty;

    [Id(12)]
    public string? CausationId { get; set; }

    [Id(13)]
    public string PayloadJson { get; set; } = "null";
}

[GenerateSerializer]
public sealed class SessionValidationRecord
{
    [Id(0)]
    public SequenceValidationStatus Status { get; set; }

    [Id(1)]
    public long Generation { get; set; }

    [Id(2)]
    public long? ClientSequence { get; set; }

    [Id(3)]
    public long? LastAcceptedClientSequence { get; set; }

    [Id(4)]
    public long? ExpectedClientSequence { get; set; }

    [Id(5)]
    public long? AppliedAcknowledgedSequence { get; set; }

    [Id(6)]
    public string? Reason { get; set; }
}

[GenerateSerializer]
public sealed class ReplayResponsePayloadRecord
{
    [Id(0)]
    public long Generation { get; set; } = SessionEnvelopeContract.InitialGeneration;

    [Id(1)]
    public long? FromSequenceInclusive { get; set; }

    [Id(2)]
    public long? ToSequenceInclusive { get; set; }

    [Id(3)]
    public long? AvailableFromSequence { get; set; }

    [Id(4)]
    public long? AvailableToSequence { get; set; }

    [Id(5)]
    public bool IsComplete { get; set; } = true;

    [Id(6)]
    public bool HasMore { get; set; }

    [Id(7)]
    public bool GapDetected { get; set; }

    [Id(8)]
    public List<SessionEnvelopeRecord> Messages { get; set; } = [];
}

[GenerateSerializer]
public sealed class SessionSequencingSnapshotRecord
{
    [Id(0)]
    public long Generation { get; set; } = SessionEnvelopeContract.InitialGeneration;

    [Id(1)]
    public long LastBrokerSequence { get; set; }

    [Id(2)]
    public long? LastClientSequence { get; set; }

    [Id(3)]
    public long? AcknowledgedSequence { get; set; }
}

[GenerateSerializer]
public sealed class SessionReplayBufferTelemetryRecord
{
    [Id(0)]
    public int Capacity { get; set; }

    [Id(1)]
    public int Count { get; set; }

    [Id(2)]
    public long? AvailableFromSequence { get; set; }

    [Id(3)]
    public long? AvailableToSequence { get; set; }
}

[GenerateSerializer]
public sealed class SessionTelemetryEnvelopeRecord
{
    [Id(0)]
    public DateTimeOffset ObservedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [Id(1)]
    public SessionMessageType MessageType { get; set; }

    [Id(2)]
    public MessageDirection Direction { get; set; }

    [Id(3)]
    public long Generation { get; set; } = SessionEnvelopeContract.InitialGeneration;

    [Id(4)]
    public long? Sequence { get; set; }

    [Id(5)]
    public long? ClientSequence { get; set; }

    [Id(6)]
    public long? AcknowledgedSequence { get; set; }

    [Id(7)]
    public string MessageId { get; set; } = string.Empty;

    [Id(8)]
    public string CorrelationId { get; set; } = string.Empty;

    [Id(9)]
    public string? CausationId { get; set; }

    [Id(10)]
    public string PayloadType { get; set; } = string.Empty;

    [Id(11)]
    public string PayloadPreview { get; set; } = string.Empty;
}

[GenerateSerializer]
public sealed class SessionTelemetryEventRecord
{
    [Id(0)]
    public DateTimeOffset ObservedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [Id(1)]
    public SessionTelemetryEventType EventType { get; set; }

    [Id(2)]
    public string Summary { get; set; } = string.Empty;

    [Id(3)]
    public SessionMessageType? MessageType { get; set; }

    [Id(4)]
    public long Generation { get; set; } = SessionEnvelopeContract.InitialGeneration;

    [Id(5)]
    public long? Sequence { get; set; }

    [Id(6)]
    public long? ClientSequence { get; set; }

    [Id(7)]
    public long? ExpectedClientSequence { get; set; }

    [Id(8)]
    public long? LastAcceptedClientSequence { get; set; }

    [Id(9)]
    public long? AcknowledgedSequence { get; set; }

    [Id(10)]
    public string? MessageId { get; set; }

    [Id(11)]
    public string? CorrelationId { get; set; }

    [Id(12)]
    public string? CausationId { get; set; }

    [Id(13)]
    public SequenceValidationStatus? ValidationStatus { get; set; }

    [Id(14)]
    public bool? GapDetected { get; set; }

    [Id(15)]
    public long? RequestedFromSequence { get; set; }

    [Id(16)]
    public long? RequestedToSequence { get; set; }

    [Id(17)]
    public long? AvailableFromSequence { get; set; }

    [Id(18)]
    public long? AvailableToSequence { get; set; }

    [Id(19)]
    public bool? HasMore { get; set; }

    [Id(20)]
    public bool? IsComplete { get; set; }

    [Id(21)]
    public string? Reason { get; set; }
}

[GenerateSerializer]
public sealed class SessionTelemetrySnapshotRecord
{
    [Id(0)]
    public int ExportVersion { get; set; } = SessionDiagnosticsDefaults.ExportVersion;

    [Id(1)]
    public DateTimeOffset ExportedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [Id(2)]
    public SessionDescriptorRecord Session { get; set; } = new();

    [Id(3)]
    public SessionSequencingSnapshotRecord Sequencing { get; set; } = new();

    [Id(4)]
    public SessionReplayBufferTelemetryRecord ReplayBuffer { get; set; } = new();

    [Id(5)]
    public List<SessionTelemetryEnvelopeRecord> RecentEnvelopes { get; set; } = [];

    [Id(6)]
    public List<SessionTelemetryEventRecord> RecentEvents { get; set; } = [];
}

[GenerateSerializer]
public sealed class SessionGrainState
{
    [Id(0)]
    public SessionDescriptorRecord? Descriptor { get; set; }

    [Id(1)]
    public long Generation { get; set; } = SessionEnvelopeContract.InitialGeneration;

    [Id(2)]
    public long NextBrokerSequence { get; set; } = 1;

    [Id(3)]
    public long? LastClientSequence { get; set; }

    [Id(4)]
    public long? AcknowledgedSequence { get; set; }

    [Id(5)]
    public List<SessionEnvelopeRecord> ReplayMessages { get; set; } = [];

    [Id(6)]
    public List<SessionTelemetryEnvelopeRecord> RecentEnvelopes { get; set; } = [];

    [Id(7)]
    public List<SessionTelemetryEventRecord> RecentEvents { get; set; } = [];
}

internal static class SessionGrainSerialization
{
    public static SessionDescriptorRecord ToRecord(this SessionDescriptor descriptor) =>
        new()
        {
            SessionId = descriptor.SessionId,
            ProjectId = descriptor.ProjectId,
            State = descriptor.State,
            CreatedAtUtc = descriptor.CreatedAtUtc
        };

    public static SessionDescriptor ToDescriptor(this SessionDescriptorRecord descriptor) =>
        new()
        {
            SessionId = descriptor.SessionId,
            ProjectId = descriptor.ProjectId,
            State = descriptor.State,
            CreatedAtUtc = descriptor.CreatedAtUtc
        };

    public static BrokerEnvelopeCommandRecord ToRecord<TPayload>(this BrokerEnvelopeCommand<TPayload> command) =>
        new()
        {
            MessageType = command.MessageType,
            MessageId = command.MessageId,
            CorrelationId = command.CorrelationId,
            CausationId = command.CausationId,
            TimestampUtc = command.TimestampUtc,
            PayloadJson = SerializePayload(command.Payload)
        };

    public static BrokerEnvelopeCommand<JsonElement> ToJsonCommand(this BrokerEnvelopeCommandRecord command) =>
        new()
        {
            MessageType = command.MessageType,
            MessageId = command.MessageId,
            CorrelationId = command.CorrelationId,
            CausationId = command.CausationId,
            TimestampUtc = command.TimestampUtc,
            Payload = DeserializeJsonElement(command.PayloadJson)
        };

    public static SessionEnvelopeRecord ToRecord<TPayload>(this MessageEnvelope<TPayload> envelope) =>
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
            PayloadJson = SerializePayload(envelope.Payload)
        };

    public static MessageEnvelope<JsonElement> ToJsonEnvelope(this SessionEnvelopeRecord envelope) =>
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
            Payload = DeserializeJsonElement(envelope.PayloadJson)
        };

    public static MessageEnvelope<TPayload> ToEnvelope<TPayload>(this SessionEnvelopeRecord envelope) =>
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
            Payload = DeserializePayload<TPayload>(envelope.PayloadJson)
        };

    public static SessionValidationRecord ToRecord(this SequenceValidationResult result) =>
        new()
        {
            Status = result.Status,
            Generation = result.Generation,
            ClientSequence = result.ClientSequence,
            LastAcceptedClientSequence = result.LastAcceptedClientSequence,
            ExpectedClientSequence = result.ExpectedClientSequence,
            AppliedAcknowledgedSequence = result.AppliedAcknowledgedSequence,
            Reason = result.Reason
        };

    public static SequenceValidationResult ToValidationResult(this SessionValidationRecord result) =>
        new()
        {
            Status = result.Status,
            Generation = result.Generation,
            ClientSequence = result.ClientSequence,
            LastAcceptedClientSequence = result.LastAcceptedClientSequence,
            ExpectedClientSequence = result.ExpectedClientSequence,
            AppliedAcknowledgedSequence = result.AppliedAcknowledgedSequence,
            Reason = result.Reason
        };

    public static ReplayResponsePayload ToPayload(this ReplayResponsePayloadRecord payload) =>
        new()
        {
            Generation = payload.Generation,
            FromSequenceInclusive = payload.FromSequenceInclusive,
            ToSequenceInclusive = payload.ToSequenceInclusive,
            AvailableFromSequence = payload.AvailableFromSequence,
            AvailableToSequence = payload.AvailableToSequence,
            GapDetected = payload.GapDetected,
            HasMore = payload.HasMore,
            IsComplete = payload.IsComplete,
            Messages = payload.Messages.Select(ToJsonEnvelope).ToArray()
        };

    public static SessionTelemetrySnapshotRecord ToRecord(this SessionTelemetrySnapshot snapshot) =>
        new()
        {
            ExportVersion = snapshot.ExportVersion,
            ExportedAtUtc = snapshot.ExportedAtUtc,
            Session = snapshot.Session.ToRecord(),
            Sequencing = snapshot.Sequencing.ToRecord(),
            ReplayBuffer = snapshot.ReplayBuffer.ToRecord(),
            RecentEnvelopes = snapshot.RecentEnvelopes.Select(ToRecord).ToList(),
            RecentEvents = snapshot.RecentEvents.Select(ToRecord).ToList()
        };

    public static SessionTelemetrySnapshot ToSnapshot(this SessionTelemetrySnapshotRecord snapshot) =>
        new()
        {
            ExportVersion = snapshot.ExportVersion,
            ExportedAtUtc = snapshot.ExportedAtUtc,
            Session = snapshot.Session.ToDescriptor(),
            Sequencing = snapshot.Sequencing.ToSnapshot(),
            ReplayBuffer = snapshot.ReplayBuffer.ToTelemetry(),
            RecentEnvelopes = snapshot.RecentEnvelopes.Select(ToTelemetry).ToArray(),
            RecentEvents = snapshot.RecentEvents.Select(ToTelemetry).ToArray()
        };

    public static SessionRuntimePersistenceSnapshot? ToRuntimeSnapshot(this SessionGrainState state)
    {
        if (state.Descriptor is null)
        {
            return null;
        }

        return new SessionRuntimePersistenceSnapshot(
            state.Descriptor.ToDescriptor(),
            state.Generation,
            state.NextBrokerSequence,
            state.LastClientSequence,
            state.AcknowledgedSequence,
            state.ReplayMessages.Select(ToJsonEnvelope).ToArray(),
            state.RecentEnvelopes.Select(ToTelemetry).ToArray(),
            state.RecentEvents.Select(ToTelemetry).ToArray());
    }

    public static SessionGrainState ToGrainState(this SessionRuntimePersistenceSnapshot snapshot) =>
        new()
        {
            Descriptor = snapshot.Descriptor.ToRecord(),
            Generation = snapshot.Generation,
            NextBrokerSequence = snapshot.NextBrokerSequence,
            LastClientSequence = snapshot.LastClientSequence,
            AcknowledgedSequence = snapshot.AcknowledgedSequence,
            ReplayMessages = snapshot.ReplayMessages.Select(ToRecord).ToList(),
            RecentEnvelopes = snapshot.RecentEnvelopes.Select(ToRecord).ToList(),
            RecentEvents = snapshot.RecentEvents.Select(ToRecord).ToList()
        };

    private static SessionSequencingSnapshotRecord ToRecord(this SessionSequencingSnapshot snapshot) =>
        new()
        {
            Generation = snapshot.Generation,
            LastBrokerSequence = snapshot.LastBrokerSequence,
            LastClientSequence = snapshot.LastClientSequence,
            AcknowledgedSequence = snapshot.AcknowledgedSequence
        };

    private static SessionSequencingSnapshot ToSnapshot(this SessionSequencingSnapshotRecord snapshot) =>
        new(
            snapshot.Generation,
            snapshot.LastBrokerSequence,
            snapshot.LastClientSequence,
            snapshot.AcknowledgedSequence);

    private static SessionReplayBufferTelemetryRecord ToRecord(this SessionReplayBufferTelemetry telemetry) =>
        new()
        {
            Capacity = telemetry.Capacity,
            Count = telemetry.Count,
            AvailableFromSequence = telemetry.AvailableFromSequence,
            AvailableToSequence = telemetry.AvailableToSequence
        };

    private static SessionReplayBufferTelemetry ToTelemetry(this SessionReplayBufferTelemetryRecord telemetry) =>
        new()
        {
            Capacity = telemetry.Capacity,
            Count = telemetry.Count,
            AvailableFromSequence = telemetry.AvailableFromSequence,
            AvailableToSequence = telemetry.AvailableToSequence
        };

    private static SessionTelemetryEnvelopeRecord ToRecord(this SessionTelemetryEnvelope telemetry) =>
        new()
        {
            ObservedAtUtc = telemetry.ObservedAtUtc,
            MessageType = telemetry.MessageType,
            Direction = telemetry.Direction,
            Generation = telemetry.Generation,
            Sequence = telemetry.Sequence,
            ClientSequence = telemetry.ClientSequence,
            AcknowledgedSequence = telemetry.AcknowledgedSequence,
            MessageId = telemetry.MessageId,
            CorrelationId = telemetry.CorrelationId,
            CausationId = telemetry.CausationId,
            PayloadType = telemetry.PayloadType,
            PayloadPreview = telemetry.PayloadPreview
        };

    private static SessionTelemetryEnvelope ToTelemetry(this SessionTelemetryEnvelopeRecord telemetry) =>
        new()
        {
            ObservedAtUtc = telemetry.ObservedAtUtc,
            MessageType = telemetry.MessageType,
            Direction = telemetry.Direction,
            Generation = telemetry.Generation,
            Sequence = telemetry.Sequence,
            ClientSequence = telemetry.ClientSequence,
            AcknowledgedSequence = telemetry.AcknowledgedSequence,
            MessageId = telemetry.MessageId,
            CorrelationId = telemetry.CorrelationId,
            CausationId = telemetry.CausationId,
            PayloadType = telemetry.PayloadType,
            PayloadPreview = telemetry.PayloadPreview
        };

    private static SessionTelemetryEventRecord ToRecord(this SessionTelemetryEvent telemetry) =>
        new()
        {
            ObservedAtUtc = telemetry.ObservedAtUtc,
            EventType = telemetry.EventType,
            Summary = telemetry.Summary,
            MessageType = telemetry.MessageType,
            Generation = telemetry.Generation,
            Sequence = telemetry.Sequence,
            ClientSequence = telemetry.ClientSequence,
            ExpectedClientSequence = telemetry.ExpectedClientSequence,
            LastAcceptedClientSequence = telemetry.LastAcceptedClientSequence,
            AcknowledgedSequence = telemetry.AcknowledgedSequence,
            MessageId = telemetry.MessageId,
            CorrelationId = telemetry.CorrelationId,
            CausationId = telemetry.CausationId,
            ValidationStatus = telemetry.ValidationStatus,
            GapDetected = telemetry.GapDetected,
            RequestedFromSequence = telemetry.RequestedFromSequence,
            RequestedToSequence = telemetry.RequestedToSequence,
            AvailableFromSequence = telemetry.AvailableFromSequence,
            AvailableToSequence = telemetry.AvailableToSequence,
            HasMore = telemetry.HasMore,
            IsComplete = telemetry.IsComplete,
            Reason = telemetry.Reason
        };

    private static SessionTelemetryEvent ToTelemetry(this SessionTelemetryEventRecord telemetry) =>
        new()
        {
            ObservedAtUtc = telemetry.ObservedAtUtc,
            EventType = telemetry.EventType,
            Summary = telemetry.Summary,
            MessageType = telemetry.MessageType,
            Generation = telemetry.Generation,
            Sequence = telemetry.Sequence,
            ClientSequence = telemetry.ClientSequence,
            ExpectedClientSequence = telemetry.ExpectedClientSequence,
            LastAcceptedClientSequence = telemetry.LastAcceptedClientSequence,
            AcknowledgedSequence = telemetry.AcknowledgedSequence,
            MessageId = telemetry.MessageId,
            CorrelationId = telemetry.CorrelationId,
            CausationId = telemetry.CausationId,
            ValidationStatus = telemetry.ValidationStatus,
            GapDetected = telemetry.GapDetected,
            RequestedFromSequence = telemetry.RequestedFromSequence,
            RequestedToSequence = telemetry.RequestedToSequence,
            AvailableFromSequence = telemetry.AvailableFromSequence,
            AvailableToSequence = telemetry.AvailableToSequence,
            HasMore = telemetry.HasMore,
            IsComplete = telemetry.IsComplete,
            Reason = telemetry.Reason
        };

    private static string SerializePayload<TPayload>(TPayload payload) =>
        JsonSerializer.Serialize(payload, SessionMessageSerializer.DefaultOptions);

    private static JsonElement DeserializeJsonElement(string payloadJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "null" : payloadJson);
        return document.RootElement.Clone();
    }

    private static TPayload DeserializePayload<TPayload>(string payloadJson) =>
        JsonSerializer.Deserialize<TPayload>(payloadJson, SessionMessageSerializer.DefaultOptions)
        ?? throw new InvalidOperationException($"Unable to deserialize session payload as {typeof(TPayload).Name}.");
}
