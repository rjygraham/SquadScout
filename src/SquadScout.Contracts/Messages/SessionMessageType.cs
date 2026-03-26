namespace SquadScout.Contracts.Messages;

public enum SessionMessageType
{
    Input = 0,
    Output = 1,
    SessionLifecycle = 2,
    ReplayRequest = 3,
    ReplayResponse = 4,
    Heartbeat = 5,
    ProjectCatalogRequest = 6,
    ProjectCatalogResponse = 7,
    StartSessionRequest = 8,
    StartSessionResponse = 9,
    SessionStatusRequest = 10,
    SessionStatusResponse = 11
}
