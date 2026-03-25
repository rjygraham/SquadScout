using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SquadScout.Contracts.Messages;

public static class SessionMessageSerializer
{
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    public static JsonSerializerOptions CreateDefaultOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }

    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.PropertyNameCaseInsensitive = false;
        options.WriteIndented = false;

        if (!options.Converters.OfType<JsonStringEnumConverter>().Any())
        {
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        }
    }
}
