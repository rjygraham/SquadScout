using System.Text.Json.Serialization;

namespace SquadScout.Contracts.Messages;

[JsonConverter(typeof(JsonStringEnumConverter<ReplayRequestReason>))]
public enum ReplayRequestReason
{
    GapDetected = 0,
    ReconnectResume = 1,
    ClientRecovery = 2
}
