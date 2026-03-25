namespace SquadScout.Contracts.Messages;

public enum SessionMessageType
{
    Input = 0,
    Output = 1,
    SessionLifecycle = 2,
    ReplayRequest = 3,
    ReplayResponse = 4,
    Heartbeat = 5
}
