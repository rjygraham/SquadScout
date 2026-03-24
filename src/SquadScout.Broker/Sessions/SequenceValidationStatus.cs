namespace SquadScout.Broker.Sessions;

public enum SequenceValidationStatus
{
    Accepted = 0,
    Duplicate = 1,
    GapDetected = 2,
    StaleGeneration = 3,
    FutureGeneration = 4,
    InvalidEnvelope = 5
}
