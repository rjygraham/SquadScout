using System.Text.Json;
using SquadScout.Contracts.Security;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Sessions;

internal sealed class SessionRuntimeState
{
    private readonly CircularReplayBuffer _replayBuffer;
    private readonly SessionTelemetryBuffer<SessionTelemetryEnvelope> _recentEnvelopes;
    private readonly SessionTelemetryBuffer<SessionTelemetryEvent> _recentEvents;

    public SessionRuntimeState(SessionDescriptor descriptor, int replayBufferCapacity)
    {
        Descriptor = descriptor;
        _replayBuffer = new CircularReplayBuffer(replayBufferCapacity);
        _recentEnvelopes = new SessionTelemetryBuffer<SessionTelemetryEnvelope>(SessionDiagnosticsDefaults.RecentEnvelopeCapacity);
        _recentEvents = new SessionTelemetryBuffer<SessionTelemetryEvent>(SessionDiagnosticsDefaults.RecentEventCapacity);
    }

    public SessionRuntimeState(SessionRuntimePersistenceSnapshot snapshot, int replayBufferCapacity)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Descriptor = snapshot.Descriptor;
        Generation = snapshot.Generation;
        NextBrokerSequence = snapshot.NextBrokerSequence;
        LastClientSequence = snapshot.LastClientSequence;
        AcknowledgedSequence = snapshot.AcknowledgedSequence;
        _replayBuffer = new CircularReplayBuffer(replayBufferCapacity, snapshot.ReplayMessages);
        _recentEnvelopes = new SessionTelemetryBuffer<SessionTelemetryEnvelope>(
            SessionDiagnosticsDefaults.RecentEnvelopeCapacity,
            snapshot.RecentEnvelopes);
        _recentEvents = new SessionTelemetryBuffer<SessionTelemetryEvent>(
            SessionDiagnosticsDefaults.RecentEventCapacity,
            snapshot.RecentEvents);
    }

    public SessionDescriptor Descriptor { get; private set; }

    public object SyncRoot { get; } = new();

    public SemaphoreSlim ClientMessageGate { get; } = new(1, 1);

    public long Generation { get; private set; } = SessionEnvelopeContract.InitialGeneration;

    public long NextBrokerSequence { get; private set; } = 1;

    public long? LastClientSequence { get; private set; }

    public long? AcknowledgedSequence { get; private set; }

    public SessionSequencingSnapshot CreateSnapshot() =>
        new(
            Generation,
            NextBrokerSequence - 1,
            LastClientSequence,
            AcknowledgedSequence);

    public SessionTelemetrySnapshot ExportTelemetry() =>
        new()
        {
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Session = Descriptor,
            Sequencing = CreateSnapshot(),
            ReplayBuffer = new SessionReplayBufferTelemetry
            {
                Capacity = _replayBuffer.Capacity,
                Count = _replayBuffer.Count,
                AvailableFromSequence = _replayBuffer.AvailableFromSequence,
                AvailableToSequence = _replayBuffer.AvailableToSequence
            },
            RecentEnvelopes = _recentEnvelopes.Snapshot(),
            RecentEvents = _recentEvents.Snapshot()
        };

    public SessionRuntimePersistenceSnapshot ExportPersistenceSnapshot() =>
        new(
            Descriptor,
            Generation,
            NextBrokerSequence,
            LastClientSequence,
            AcknowledgedSequence,
            _replayBuffer.Snapshot(),
            _recentEnvelopes.Snapshot(),
            _recentEvents.Snapshot());

    public void RecordSessionStarted(string? requestedBy)
    {
        _recentEvents.Append(new SessionTelemetryEvent
        {
            EventType = SessionTelemetryEventType.SessionStarted,
            Summary = $"Session '{Descriptor.SessionId}' started for project '{Descriptor.ProjectId}'.",
            Generation = Generation,
            Reason = string.IsNullOrWhiteSpace(requestedBy)
                ? null
                : $"Requested by {RedactString(requestedBy)}."
        });
    }

    public void ApplyValidationResult(SequenceValidationResult result)
    {
        if (!result.IsAccepted)
        {
            return;
        }

        if (result.Status == SequenceValidationStatus.Accepted)
        {
            LastClientSequence = result.ClientSequence ?? LastClientSequence;
        }

        if (result.AppliedAcknowledgedSequence is long acknowledgedSequence)
        {
            AcknowledgedSequence = acknowledgedSequence;
        }
    }

    public long ResetGeneration()
    {
        var previousGeneration = Generation;
        Generation++;
        NextBrokerSequence = 1;
        LastClientSequence = null;
        AcknowledgedSequence = null;
        _replayBuffer.Clear();
        _recentEvents.Append(new SessionTelemetryEvent
        {
            EventType = SessionTelemetryEventType.GenerationReset,
            Summary = $"Ordered replay state reset for session '{Descriptor.SessionId}'.",
            Generation = Generation,
            Reason = $"Generation advanced from {previousGeneration} to {Generation}."
        });

        return Generation;
    }

    public MessageEnvelope<TPayload> CreateBrokerEnvelope<TPayload>(BrokerEnvelopeCommand<TPayload> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.MessageId))
        {
            throw new ArgumentException("A broker message id is required.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.CorrelationId))
        {
            throw new ArgumentException("A correlation id is required.", nameof(command));
        }

        var envelope = new MessageEnvelope<TPayload>
        {
            ProjectId = Descriptor.ProjectId,
            SessionId = Descriptor.SessionId,
            Generation = Generation,
            MessageType = command.MessageType,
            Direction = MessageDirection.BrokerToClient,
            Sequence = IsReplayableBrokerMessage(command.MessageType) ? NextBrokerSequence++ : null,
            AcknowledgedSequence = AcknowledgedSequence,
            TimestampUtc = command.TimestampUtc ?? DateTimeOffset.UtcNow,
            MessageId = command.MessageId,
            CorrelationId = command.CorrelationId,
            CausationId = command.CausationId,
            Payload = command.Payload
        };

        if (envelope.Sequence is not null)
        {
            _replayBuffer.Append(ToSnapshot(envelope));
        }

        if (command.MessageType == SessionMessageType.SessionLifecycle && command.Payload is SessionLifecyclePayload lifecycle)
        {
            Descriptor = Descriptor with { State = lifecycle.State };
        }

        RecordEnvelopeObserved(envelope);
        return envelope;
    }

    public MessageEnvelope<ReplayResponsePayload> CreateReplayResponse(MessageEnvelope<ReplayRequestPayload> request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Generation != Generation)
        {
            return CreateGenerationResetBoundaryResponse(request);
        }

        var readResult = _replayBuffer.ReadWindow(
            request.Payload.FromSequenceInclusive,
            request.Payload.ToSequenceInclusive,
            request.Payload.MaximumMessages);

        var payload = new ReplayResponsePayload
        {
            Generation = Generation,
            FromSequenceInclusive = readResult.FromSequenceInclusive,
            ToSequenceInclusive = readResult.ToSequenceInclusive,
            AvailableFromSequence = readResult.AvailableFromSequence,
            AvailableToSequence = readResult.AvailableToSequence,
            GapDetected = readResult.GapDetected,
            HasMore = readResult.HasMore,
            IsComplete = readResult.IsComplete,
            Messages = readResult.Messages
        };

        return CreateReplayEnvelope(request, payload);
    }

    private MessageEnvelope<ReplayResponsePayload> CreateGenerationResetBoundaryResponse(MessageEnvelope<ReplayRequestPayload> request) =>
        CreateReplayEnvelope(request, new ReplayResponsePayload
        {
            Generation = Generation,
            AvailableFromSequence = _replayBuffer.AvailableFromSequence,
            AvailableToSequence = _replayBuffer.AvailableToSequence,
            GapDetected = true,
            IsComplete = true,
            HasMore = false,
            Messages = Array.Empty<MessageEnvelope<JsonElement>>()
        });

    private MessageEnvelope<ReplayResponsePayload> CreateReplayEnvelope(
        MessageEnvelope<ReplayRequestPayload> request,
        ReplayResponsePayload payload)
    {
        var envelope = new MessageEnvelope<ReplayResponsePayload>
        {
            ProjectId = Descriptor.ProjectId,
            SessionId = Descriptor.SessionId,
            Generation = Generation,
            MessageType = SessionMessageType.ReplayResponse,
            Direction = MessageDirection.BrokerToClient,
            AcknowledgedSequence = AcknowledgedSequence,
            TimestampUtc = DateTimeOffset.UtcNow,
            MessageId = Guid.NewGuid().ToString("n"),
            CorrelationId = request.CorrelationId,
            CausationId = request.MessageId,
            Payload = payload
        };

        RecordEnvelopeObserved(envelope);
        _recentEvents.Append(new SessionTelemetryEvent
        {
            EventType = SessionTelemetryEventType.ReplayResponseCreated,
            Summary = payload.GapDetected
                ? $"Replay for session '{Descriptor.SessionId}' returned a gap or reset boundary."
                : $"Replay for session '{Descriptor.SessionId}' returned {payload.Messages.Count} message(s).",
            MessageType = SessionMessageType.ReplayResponse,
            Generation = Generation,
            AcknowledgedSequence = AcknowledgedSequence,
            MessageId = RedactString(envelope.MessageId),
            CorrelationId = RedactString(envelope.CorrelationId),
            CausationId = RedactNullable(envelope.CausationId),
            GapDetected = payload.GapDetected,
            RequestedFromSequence = request.Payload.FromSequenceInclusive,
            RequestedToSequence = request.Payload.ToSequenceInclusive,
            AvailableFromSequence = payload.AvailableFromSequence,
            AvailableToSequence = payload.AvailableToSequence,
            HasMore = payload.HasMore,
            IsComplete = payload.IsComplete,
            Reason = request.Generation == Generation
                ? null
                : $"Replay request generation {request.Generation} does not match the active generation {Generation}."
        });

        return envelope;
    }

    public void RecordEnvelopeObserved<TPayload>(MessageEnvelope<TPayload> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        _recentEnvelopes.Append(new SessionTelemetryEnvelope
        {
            ObservedAtUtc = envelope.TimestampUtc,
            MessageType = envelope.MessageType,
            Direction = envelope.Direction,
            Generation = envelope.Generation,
            Sequence = envelope.Sequence,
            ClientSequence = envelope.ClientSequence,
            AcknowledgedSequence = envelope.AcknowledgedSequence,
            MessageId = RedactString(envelope.MessageId),
            CorrelationId = RedactString(envelope.CorrelationId),
            CausationId = RedactNullable(envelope.CausationId),
            PayloadType = typeof(TPayload).Name,
            PayloadPreview = CreatePayloadPreview(envelope.Payload)
        });
    }

    public void RecordClientValidation<TPayload>(MessageEnvelope<TPayload> envelope, SequenceValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(result);

        _recentEvents.Append(new SessionTelemetryEvent
        {
            EventType = SessionTelemetryEventType.ClientEnvelopeValidated,
            Summary = CreateValidationSummary(envelope, result),
            MessageType = envelope.MessageType,
            Generation = result.Generation,
            ClientSequence = result.ClientSequence,
            ExpectedClientSequence = result.ExpectedClientSequence,
            LastAcceptedClientSequence = result.LastAcceptedClientSequence,
            AcknowledgedSequence = result.AppliedAcknowledgedSequence,
            MessageId = RedactString(envelope.MessageId),
            CorrelationId = RedactString(envelope.CorrelationId),
            CausationId = RedactNullable(envelope.CausationId),
            ValidationStatus = result.Status,
            GapDetected = result.Status == SequenceValidationStatus.GapDetected,
            Reason = RedactNullable(result.Reason)
        });
    }

    public void RecordClientForwardFailure<TPayload>(
        MessageEnvelope<TPayload> envelope,
        SequenceValidationResult result,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(exception);

        _recentEvents.Append(new SessionTelemetryEvent
        {
            EventType = SessionTelemetryEventType.ClientEnvelopeForwardFailed,
            Summary = $"Forwarding client envelope '{RedactString(envelope.MessageId)}' to the session PTY failed.",
            MessageType = envelope.MessageType,
            Generation = result.Generation,
            ClientSequence = result.ClientSequence,
            ExpectedClientSequence = result.ExpectedClientSequence,
            LastAcceptedClientSequence = result.LastAcceptedClientSequence,
            AcknowledgedSequence = result.AppliedAcknowledgedSequence,
            MessageId = RedactString(envelope.MessageId),
            CorrelationId = RedactString(envelope.CorrelationId),
            CausationId = RedactNullable(envelope.CausationId),
            ValidationStatus = result.Status,
            Reason = RedactString(exception.Message)
        });
    }

    private static bool IsReplayableBrokerMessage(SessionMessageType messageType) =>
        messageType is SessionMessageType.Output or SessionMessageType.SessionLifecycle;

    private static MessageEnvelope<JsonElement> ToSnapshot<TPayload>(MessageEnvelope<TPayload> envelope) =>
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
            Payload = envelope.Payload is JsonElement payload
                ? payload.Clone()
                : JsonSerializer.SerializeToElement(envelope.Payload, SessionMessageSerializer.DefaultOptions)
        };

    private static string CreateValidationSummary<TPayload>(MessageEnvelope<TPayload> envelope, SequenceValidationResult result) =>
        result.Status switch
        {
            SequenceValidationStatus.Accepted =>
                $"Accepted client {envelope.MessageType} envelope '{RedactString(envelope.MessageId)}'.",
            SequenceValidationStatus.Duplicate =>
                $"Ignored duplicate client {envelope.MessageType} envelope '{RedactString(envelope.MessageId)}'.",
            SequenceValidationStatus.GapDetected =>
                $"Accepted client {envelope.MessageType} envelope '{RedactString(envelope.MessageId)}' after detecting a sequence gap.",
            SequenceValidationStatus.StaleGeneration =>
                $"Rejected client {envelope.MessageType} envelope '{RedactString(envelope.MessageId)}' because it targeted a stale generation.",
            SequenceValidationStatus.FutureGeneration =>
                $"Rejected client {envelope.MessageType} envelope '{RedactString(envelope.MessageId)}' because it targeted a future generation.",
            _ =>
                $"Rejected client {envelope.MessageType} envelope '{RedactString(envelope.MessageId)}' as invalid."
        };

    private static string CreatePayloadPreview<TPayload>(TPayload payload)
    {
        if (payload is null)
        {
            return string.Empty;
        }

        var jsonElement = payload is JsonElement existing
            ? existing
            : JsonSerializer.SerializeToElement(payload, SessionMessageSerializer.DefaultOptions);

        var preview = SecretRedactor.Redact(jsonElement).GetRawText();

        return preview.Length <= SessionDiagnosticsDefaults.PayloadPreviewCharacterLimit
            ? preview
            : $"{preview[..SessionDiagnosticsDefaults.PayloadPreviewCharacterLimit]}…";
    }

    private static string RedactString(string value) => SecretRedactor.Redact(value ?? string.Empty);

    private static string? RedactNullable(string? value) =>
        string.IsNullOrEmpty(value)
            ? value
            : SecretRedactor.Redact(value);
}
