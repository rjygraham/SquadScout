using SquadScout.Broker.Relay;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests.TestDoubles;

public sealed class RecordingSessionGroupResolver : ISessionGroupResolver
{
    private readonly object _syncRoot = new();
    private readonly SessionGroupResolver _inner = new();
    private readonly List<string> _resolvedSessionGroups = [];
    private readonly List<string> _resolvedEnvelopeGroups = [];

    public IReadOnlyList<string> ResolvedSessionGroups
    {
        get
        {
            lock (_syncRoot)
            {
                return _resolvedSessionGroups.ToArray();
            }
        }
    }

    public IReadOnlyList<string> ResolvedEnvelopeGroups
    {
        get
        {
            lock (_syncRoot)
            {
                return _resolvedEnvelopeGroups.ToArray();
            }
        }
    }

    public string Resolve(SessionDescriptor session)
    {
        var sessionGroup = _inner.Resolve(session);
        lock (_syncRoot)
        {
            _resolvedSessionGroups.Add(sessionGroup);
        }

        return sessionGroup;
    }

    public string Resolve<TPayload>(MessageEnvelope<TPayload> envelope)
    {
        var sessionGroup = _inner.Resolve(envelope);
        lock (_syncRoot)
        {
            _resolvedEnvelopeGroups.Add(sessionGroup);
        }

        return sessionGroup;
    }
}
