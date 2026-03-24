using System.Text.Json;
using System.Text.Json.Serialization;

namespace SquadScout.Contracts.Messages;

public static class SessionMessageSerializer
{
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    public static JsonSerializerOptions CreateDefaultOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            WriteIndented = false
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
