using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Realtime;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Relay;

public sealed class SessionGroupResolver : ISessionGroupResolver
{
    public string Resolve(SessionDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return SessionGroupName.Create(session.ProjectId, session.SessionId);
    }

    public string Resolve<TPayload>(MessageEnvelope<TPayload> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return SessionGroupName.Create(envelope.ProjectId, envelope.SessionId);
    }
}
