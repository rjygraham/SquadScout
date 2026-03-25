using SquadScout.Contracts.Messages;

namespace SquadScout.Contracts.Realtime;

public static class SessionUpstreamEventNames
{
    public const string Input = "session-input";

    public static string Resolve(SessionMessageType messageType) =>
        messageType switch
        {
            SessionMessageType.Input => Input,
            _ => throw new ArgumentOutOfRangeException(
                nameof(messageType),
                messageType,
                "No Azure Web PubSub upstream event is defined for this session message type.")
        };
}
