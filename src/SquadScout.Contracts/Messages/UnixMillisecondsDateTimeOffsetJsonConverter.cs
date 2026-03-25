using System.Text.Json;
using System.Text.Json.Serialization;

namespace SquadScout.Contracts.Messages;

/// <summary>
/// Serializes DateTimeOffset as Unix milliseconds (13 digits) instead of ISO8601/RFC3339 (25 chars)
/// to reduce Web PubSub outbound message size. Supports dual-format deserialization for backward compatibility.
/// </summary>
public sealed class UnixMillisecondsDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                // Unix milliseconds format (new)
                var milliseconds = reader.GetInt64();
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

            case JsonTokenType.String:
                // ISO8601/RFC3339 format (legacy, backward compatibility)
                var dateString = reader.GetString();
                if (DateTimeOffset.TryParse(dateString, out var date))
                {
                    return date;
                }
                throw new JsonException($"Unable to parse DateTimeOffset from string: {dateString}");

            default:
                throw new JsonException($"Expected number or string token for DateTimeOffset, got {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.ToUnixTimeMilliseconds());
    }
}
