using System.Text.Json.Serialization;

namespace SquadScout.Contracts.Messages;

[JsonConverter(typeof(JsonStringEnumConverter<SessionMessageType>))]
public enum SessionMessageType
{
    Input = 0,
    Output = 1,
    SessionLifecycle = 2,
    ReplayRequest = 3,
    ReplayResponse = 4,
    Heartbeat = 5
}
