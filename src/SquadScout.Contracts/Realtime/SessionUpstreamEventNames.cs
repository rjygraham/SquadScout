using SquadScout.Contracts.Messages;

namespace SquadScout.Contracts.Realtime;

public static class SessionUpstreamEventNames
{
    public const string Heartbeat = "session-heartbeat";

    public const string Input = "session-input";
    public const string ReplayRequest = "session-replay";

    public static string Resolve(SessionMessageType messageType) =>
        messageType switch
        {
            SessionMessageType.Heartbeat => Heartbeat,
            SessionMessageType.Input => Input,
            SessionMessageType.ReplayRequest => ReplayRequest,
            _ => throw new ArgumentOutOfRangeException(
                nameof(messageType),
                messageType,
                "No Azure Web PubSub upstream event is defined for this session message type.")
        };
}
