using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Relay;

public interface ISessionGroupResolver
{
    string Resolve(SessionDescriptor session);

    string Resolve<TPayload>(MessageEnvelope<TPayload> envelope);
}
