using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Sessions;

public interface ISequenceValidator
{
    SequenceValidationResult Validate<TPayload>(SessionSequencingSnapshot snapshot, MessageEnvelope<TPayload> envelope);
}
