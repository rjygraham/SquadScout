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
