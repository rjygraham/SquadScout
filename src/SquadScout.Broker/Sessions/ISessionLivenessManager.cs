using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Sessions;

public interface ISessionLivenessManager
{
    TimeSpan HeartbeatInterval { get; }

    TimeSpan LivenessTimeout { get; }

    void RegisterSession(string sessionId);

    void UnregisterSession(string sessionId);

    HeartbeatPayload IssueHeartbeat(string sessionId);

    bool CanAcceptHeartbeat(string sessionId, HeartbeatPayload payload, out string validationError);

    bool TryCommitHeartbeat(string sessionId, HeartbeatPayload payload);

    void RecordClientActivity(string sessionId);

    bool HasTimedOut(string sessionId);
}
