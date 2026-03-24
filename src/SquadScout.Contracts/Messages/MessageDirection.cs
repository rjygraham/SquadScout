using System.Text.Json.Serialization;

namespace SquadScout.Contracts.Messages;

[JsonConverter(typeof(JsonStringEnumConverter<MessageDirection>))]
public enum MessageDirection
{
    ClientToBroker = 0,
    BrokerToClient = 1
}
