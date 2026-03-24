using System.Text.Json;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Sessions;

internal sealed class SessionRuntimeState
{
    private readonly CircularReplayBuffer _replayBuffer;

    public SessionRuntimeState(SessionDescriptor descriptor, int replayBufferCapacity)
    {
        Descriptor = descriptor;
        _replayBuffer = new CircularReplayBuffer(replayBufferCapacity);
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
        Generation++;
        NextBrokerSequence = 1;
        LastClientSequence = null;
        AcknowledgedSequence = null;
        _replayBuffer.Clear();

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

        return CreateReplayEnvelope(request, new ReplayResponsePayload
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
        });
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
        ReplayResponsePayload payload) =>
        new()
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
}
