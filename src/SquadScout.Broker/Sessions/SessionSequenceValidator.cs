using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Sessions;

public sealed class SessionSequenceValidator : ISequenceValidator
{
    public SequenceValidationResult Validate<TPayload>(SessionSequencingSnapshot snapshot, MessageEnvelope<TPayload> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Direction != MessageDirection.ClientToBroker)
        {
            return Invalid(snapshot, envelope, "Only client-to-broker envelopes can be validated.");
        }

        if (envelope.Sequence is not null)
        {
            return Invalid(snapshot, envelope, "Client envelopes must not set broker-owned sequence values.");
        }

        if (envelope.Generation < snapshot.Generation)
        {
            return new SequenceValidationResult
            {
                Status = SequenceValidationStatus.StaleGeneration,
                Generation = snapshot.Generation,
                ClientSequence = envelope.ClientSequence,
                LastAcceptedClientSequence = snapshot.LastClientSequence,
                ExpectedClientSequence = GetExpectedClientSequence(snapshot.LastClientSequence),
                AppliedAcknowledgedSequence = snapshot.AcknowledgedSequence,
                Reason = "The envelope targets an older generation."
            };
        }

        if (envelope.Generation > snapshot.Generation)
        {
            return new SequenceValidationResult
            {
                Status = SequenceValidationStatus.FutureGeneration,
                Generation = snapshot.Generation,
                ClientSequence = envelope.ClientSequence,
                LastAcceptedClientSequence = snapshot.LastClientSequence,
                ExpectedClientSequence = GetExpectedClientSequence(snapshot.LastClientSequence),
                AppliedAcknowledgedSequence = snapshot.AcknowledgedSequence,
                Reason = "The envelope targets a future generation."
            };
        }

        var sequenceStatus = GetClientSequenceStatus(snapshot.LastClientSequence, envelope.ClientSequence, out var expectedClientSequence);
        if (sequenceStatus == SequenceValidationStatus.InvalidEnvelope)
        {
            return Invalid(snapshot, envelope, "Client sequence values must be positive when present.");
        }

        if (TryValidateAcknowledgement(snapshot, envelope, sequenceStatus, out var appliedAcknowledgedSequence, out var acknowledgementError))
        {
            return new SequenceValidationResult
            {
                Status = sequenceStatus,
                Generation = snapshot.Generation,
                ClientSequence = envelope.ClientSequence,
                LastAcceptedClientSequence = snapshot.LastClientSequence,
                ExpectedClientSequence = expectedClientSequence,
                AppliedAcknowledgedSequence = appliedAcknowledgedSequence,
                Reason = sequenceStatus switch
                {
                    SequenceValidationStatus.Duplicate => "Duplicate client envelope observed.",
                    SequenceValidationStatus.GapDetected => "A client envelope gap was detected.",
                    _ => null
                }
            };
        }

        return Invalid(snapshot, envelope, acknowledgementError);
    }

    private static bool TryValidateAcknowledgement<TPayload>(
        SessionSequencingSnapshot snapshot,
        MessageEnvelope<TPayload> envelope,
        SequenceValidationStatus sequenceStatus,
        out long? appliedAcknowledgedSequence,
        out string acknowledgementError)
    {
        appliedAcknowledgedSequence = snapshot.AcknowledgedSequence;
        acknowledgementError = string.Empty;

        if (envelope.AcknowledgedSequence is null)
        {
            return true;
        }

        if (envelope.AcknowledgedSequence <= 0)
        {
            acknowledgementError = "Acknowledged sequence values must be positive when present.";
            return false;
        }

        if (envelope.AcknowledgedSequence > snapshot.LastBrokerSequence)
        {
            acknowledgementError = "Acknowledged sequence cannot exceed the broker's emitted sequence.";
            return false;
        }

        if (sequenceStatus == SequenceValidationStatus.GapDetected)
        {
            return true;
        }

        if (sequenceStatus != SequenceValidationStatus.Duplicate &&
            snapshot.AcknowledgedSequence is long currentAcknowledgedSequence &&
            envelope.AcknowledgedSequence < currentAcknowledgedSequence)
        {
            acknowledgementError = "Acknowledged sequence cannot move backwards within a generation.";
            return false;
        }

        appliedAcknowledgedSequence = Math.Max(snapshot.AcknowledgedSequence ?? 0, envelope.AcknowledgedSequence.Value);
        return true;
    }

    private static SequenceValidationStatus GetClientSequenceStatus(
        long? lastClientSequence,
        long? currentClientSequence,
        out long? expectedClientSequence)
    {
        expectedClientSequence = GetExpectedClientSequence(lastClientSequence);

        if (currentClientSequence is null)
        {
            return SequenceValidationStatus.Accepted;
        }

        if (currentClientSequence <= 0)
        {
            return SequenceValidationStatus.InvalidEnvelope;
        }

        if (lastClientSequence is null)
        {
            return currentClientSequence == 1
                ? SequenceValidationStatus.Accepted
                : SequenceValidationStatus.GapDetected;
        }

        if (currentClientSequence == lastClientSequence + 1)
        {
            return SequenceValidationStatus.Accepted;
        }

        if (currentClientSequence <= lastClientSequence)
        {
            return SequenceValidationStatus.Duplicate;
        }

        return SequenceValidationStatus.GapDetected;
    }

    private static long? GetExpectedClientSequence(long? lastClientSequence) =>
        lastClientSequence is null ? 1 : lastClientSequence + 1;

    private static SequenceValidationResult Invalid<TPayload>(
        SessionSequencingSnapshot snapshot,
        MessageEnvelope<TPayload> envelope,
        string reason) =>
        new()
        {
            Status = SequenceValidationStatus.InvalidEnvelope,
            Generation = snapshot.Generation,
            ClientSequence = envelope.ClientSequence,
            LastAcceptedClientSequence = snapshot.LastClientSequence,
            ExpectedClientSequence = GetExpectedClientSequence(snapshot.LastClientSequence),
            AppliedAcknowledgedSequence = snapshot.AcknowledgedSequence,
            Reason = reason
        };
}
