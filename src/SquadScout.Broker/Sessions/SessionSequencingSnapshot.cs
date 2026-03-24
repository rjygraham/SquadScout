namespace SquadScout.Broker.Sessions;

public sealed record SessionSequencingSnapshot(
    long Generation,
    long LastBrokerSequence,
    long? LastClientSequence,
    long? AcknowledgedSequence);
