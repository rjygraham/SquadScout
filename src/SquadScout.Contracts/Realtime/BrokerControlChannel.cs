using SquadScout.Contracts.Sessions;

namespace SquadScout.Contracts.Realtime;

public static class BrokerControlChannel
{
    public const string ProjectId = "broker-control";
    public const string SessionId = "phase1";

    public static SessionDescriptor CreateDescriptor() =>
        new()
        {
            ProjectId = ProjectId,
            SessionId = SessionId,
            State = SessionState.Pending
        };
}
