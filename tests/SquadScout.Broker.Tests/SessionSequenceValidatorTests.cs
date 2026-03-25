using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Tests;

public sealed class SessionSequenceValidatorTests
{
    private readonly SessionSequenceValidator _validator = new();

    [Fact]
    public void AcceptsFirstClientSequenceAndCumulativeAcknowledgement()
    {
        var result = _validator.Validate(
            new SessionSequencingSnapshot(
                SessionEnvelopeContract.InitialGeneration,
                LastBrokerSequence: 5,
                LastClientSequence: null,
                AcknowledgedSequence: null),
            CreateClientHeartbeat(clientSequence: 1, acknowledgedSequence: 3));

        Assert.Equal(SequenceValidationStatus.Accepted, result.Status);
        Assert.Equal(1, result.ExpectedClientSequence);
        Assert.Equal(3, result.AppliedAcknowledgedSequence);
        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void DuplicateClientSequenceRemainsIdempotent()
    {
        var result = _validator.Validate(
            new SessionSequencingSnapshot(
                SessionEnvelopeContract.InitialGeneration,
                LastBrokerSequence: 5,
                LastClientSequence: 2,
                AcknowledgedSequence: 3),
            CreateClientHeartbeat(clientSequence: 2, acknowledgedSequence: 3));

        Assert.Equal(SequenceValidationStatus.Duplicate, result.Status);
        Assert.Equal(3, result.AppliedAcknowledgedSequence);
        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void RejectsAcknowledgementRegressionForNewClientMessages()
    {
        var result = _validator.Validate(
            new SessionSequencingSnapshot(
                SessionEnvelopeContract.InitialGeneration,
                LastBrokerSequence: 5,
                LastClientSequence: 2,
                AcknowledgedSequence: 4),
            CreateClientHeartbeat(clientSequence: 3, acknowledgedSequence: 3));

        Assert.Equal(SequenceValidationStatus.InvalidEnvelope, result.Status);
        Assert.Contains("move backwards", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DetectsGapsInClientSequenceProgression()
    {
        var result = _validator.Validate(
            new SessionSequencingSnapshot(
                SessionEnvelopeContract.InitialGeneration,
                LastBrokerSequence: 5,
                LastClientSequence: 2,
                AcknowledgedSequence: 2),
            CreateClientHeartbeat(clientSequence: 4, acknowledgedSequence: 4));

        Assert.Equal(SequenceValidationStatus.GapDetected, result.Status);
        Assert.Contains("gap", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, result.AppliedAcknowledgedSequence);
    }

    [Fact]
    public void SurfacesStaleGenerationWithoutMutatingAcknowledgementState()
    {
        var result = _validator.Validate(
            new SessionSequencingSnapshot(
                Generation: 3,
                LastBrokerSequence: 9,
                LastClientSequence: 4,
                AcknowledgedSequence: 9),
            CreateClientHeartbeat(clientSequence: 5, acknowledgedSequence: 9, generation: 2));

        Assert.Equal(SequenceValidationStatus.StaleGeneration, result.Status);
        Assert.Equal(9, result.AppliedAcknowledgedSequence);
        Assert.Equal(5, result.ExpectedClientSequence);
    }

    [Fact]
    public void SurfacesFutureGenerationWithoutMutatingAcknowledgementState()
    {
        var result = _validator.Validate(
            new SessionSequencingSnapshot(
                Generation: 3,
                LastBrokerSequence: 9,
                LastClientSequence: 4,
                AcknowledgedSequence: 9),
            CreateClientHeartbeat(clientSequence: 5, acknowledgedSequence: 9, generation: 4));

        Assert.Equal(SequenceValidationStatus.FutureGeneration, result.Status);
        Assert.Equal(9, result.AppliedAcknowledgedSequence);
        Assert.Equal(5, result.ExpectedClientSequence);
    }

    [Fact]
    public void DuplicateDoesNotAdvanceAcknowledgementBeyondSnapshot()
    {
        // A retransmitted duplicate may carry a higher ack than the snapshot's current value.
        // The validator must apply the max so cumulative acks remain monotonic, but the
        // snapshot ack must never regress. This proves the idempotent ack path is safe.
        var result = _validator.Validate(
            new SessionSequencingSnapshot(
                SessionEnvelopeContract.InitialGeneration,
                LastBrokerSequence: 10,
                LastClientSequence: 3,
                AcknowledgedSequence: 5),
            CreateClientHeartbeat(clientSequence: 3, acknowledgedSequence: 7));

        Assert.Equal(SequenceValidationStatus.Duplicate, result.Status);
        Assert.True(result.IsAccepted);
        Assert.Equal(7, result.AppliedAcknowledgedSequence);
    }

    [Fact]
    public void GapDetectedDoesNotAdvanceAcknowledgement()
    {
        // When client sequence has a gap, the ack carried on the envelope is intentionally
        // ignored. This prevents a reordered gap-frame from advancing the ack past the
        // point where the broker can still detect the gap on the missing frame.
        var result = _validator.Validate(
            new SessionSequencingSnapshot(
                SessionEnvelopeContract.InitialGeneration,
                LastBrokerSequence: 10,
                LastClientSequence: 2,
                AcknowledgedSequence: 4),
            CreateClientHeartbeat(clientSequence: 5, acknowledgedSequence: 8));

        Assert.Equal(SequenceValidationStatus.GapDetected, result.Status);
        Assert.False(result.IsAccepted);
        Assert.Equal(4, result.AppliedAcknowledgedSequence);
    }

    [Fact]
    public void RejectsBrokerToClientEnvelopeDirection()
    {
        var result = _validator.Validate(
            new SessionSequencingSnapshot(
                SessionEnvelopeContract.InitialGeneration,
                LastBrokerSequence: 5,
                LastClientSequence: null,
                AcknowledgedSequence: null),
            new MessageEnvelope<HeartbeatPayload>
            {
                ProjectId = "broker",
                SessionId = "session-123",
                Generation = SessionEnvelopeContract.InitialGeneration,
                MessageType = SessionMessageType.Heartbeat,
                Direction = MessageDirection.BrokerToClient,
                ClientSequence = 1,
                MessageId = "broker-1",
                CorrelationId = "corr-heartbeat",
                Payload = new HeartbeatPayload()
            });

        Assert.Equal(SequenceValidationStatus.InvalidEnvelope, result.Status);
        Assert.False(result.IsAccepted);
        Assert.Contains("client-to-broker", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsClientEnvelopeWithBrokerOwnedSequence()
    {
        var result = _validator.Validate(
            new SessionSequencingSnapshot(
                SessionEnvelopeContract.InitialGeneration,
                LastBrokerSequence: 5,
                LastClientSequence: null,
                AcknowledgedSequence: null),
            new MessageEnvelope<HeartbeatPayload>
            {
                ProjectId = "broker",
                SessionId = "session-123",
                Generation = SessionEnvelopeContract.InitialGeneration,
                MessageType = SessionMessageType.Heartbeat,
                Direction = MessageDirection.ClientToBroker,
                Sequence = 10,
                ClientSequence = 1,
                MessageId = "client-1",
                CorrelationId = "corr-heartbeat",
                Payload = new HeartbeatPayload()
            });

        Assert.Equal(SequenceValidationStatus.InvalidEnvelope, result.Status);
        Assert.False(result.IsAccepted);
        Assert.Contains("broker-owned", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static MessageEnvelope<HeartbeatPayload> CreateClientHeartbeat(
        long clientSequence,
        long acknowledgedSequence,
        long generation = SessionEnvelopeContract.InitialGeneration) =>
        new()
        {
            ProjectId = "broker",
            SessionId = "session-123",
            Generation = generation,
            MessageType = SessionMessageType.Heartbeat,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = clientSequence,
            AcknowledgedSequence = acknowledgedSequence,
            MessageId = $"client-{clientSequence}",
            CorrelationId = "corr-heartbeat",
            Payload = new HeartbeatPayload()
        };
}
