using System.Text.Json;
using System.Text.Json.Serialization;

namespace SquadScout.Contracts.Messages;

/// <summary>
/// Serializes enums as numbers but accepts both numbers and strings during deserialization
/// for backward compatibility. This allows old clients to send "heartbeat" while new clients
/// receive 5, reducing message size.
/// </summary>
public sealed class NumberWithStringFallbackEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(NumberWithStringFallbackEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter?)Activator.CreateInstance(converterType);
    }
}

internal sealed class NumberWithStringFallbackEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                // New format: numeric value
                var intValue = reader.GetInt32();
                if (Enum.IsDefined(typeof(TEnum), intValue))
                {
                    return (TEnum)(object)intValue;
                }
                throw new JsonException($"Value {intValue} is not defined in enum {typeof(TEnum).Name}");

            case JsonTokenType.String:
                // Legacy format: camelCase string (backward compatibility)
                var stringValue = reader.GetString();
                if (string.IsNullOrEmpty(stringValue))
                {
                    throw new JsonException($"Cannot parse empty string as enum {typeof(TEnum).Name}");
                }
                
                // Try parsing with case-insensitive matching
                if (Enum.TryParse<TEnum>(stringValue, ignoreCase: true, out var result))
                {
                    return result;
                }
                throw new JsonException($"Unable to parse '{stringValue}' as enum {typeof(TEnum).Name}");

            default:
                throw new JsonException($"Expected number or string token for enum {typeof(TEnum).Name}, got {reader.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        // Always write as number
        writer.WriteNumberValue(Convert.ToInt32(value));
    }
}
