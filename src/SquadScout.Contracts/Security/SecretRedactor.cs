using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SquadScout.Contracts.Security;

/// <summary>
/// Redacts common secret and connection-bearing values before they are written to logs or diagnostics.
/// </summary>
public static partial class SecretRedactor
{
    public const string RedactedValue = "[REDACTED]";

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var redacted = value;
        redacted = SensitiveKeyValuePattern().Replace(
            redacted,
            match =>
            {
                var valueGroup = match.Groups["value"].Value;
                var redactedValue = valueGroup.Length > 1 && valueGroup[0] == valueGroup[^1] && (valueGroup[0] == '"' || valueGroup[0] == '\'')
                    ? $"{valueGroup[0]}{RedactedValue}{valueGroup[0]}"
                    : RedactedValue;

                return $"{match.Groups["prefix"].Value}{match.Groups["key"].Value}{match.Groups["suffix"].Value}{match.Groups["separator"].Value}{redactedValue}";
            });

        redacted = AuthorizationHeaderPattern().Replace(
            redacted,
            match => $"{match.Groups["scheme"].Value} {RedactedValue}");

        redacted = ConnectionStringSecretPattern().Replace(
            redacted,
            match => $"{match.Groups["key"].Value}={RedactedValue}");

        redacted = CredentialedUriPattern().Replace(
            redacted,
            match => $"{match.Groups["scheme"].Value}://{RedactedValue}@");

        redacted = SensitiveQueryValuePattern().Replace(
            redacted,
            match => $"{match.Groups["prefix"].Value}{RedactedValue}");

        redacted = JwtTokenPattern().Replace(redacted, RedactedValue);
        redacted = GitHubTokenPattern().Replace(redacted, RedactedValue);

        return redacted;
    }

    public static JsonElement Redact(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteRedactedJson(writer, value, propertyName: null);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteRedactedJson(Utf8JsonWriter writer, JsonElement value, string? propertyName)
    {
        if (propertyName is not null && SensitivePropertyNamePattern().IsMatch(propertyName))
        {
            writer.WriteStringValue(RedactedValue);
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedactedJson(writer, property.Value, property.Name);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteRedactedJson(writer, item, propertyName);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(Redact(value.GetString()));
                break;

            default:
                value.WriteTo(writer);
                break;
        }
    }

    [GeneratedRegex(
        @"(?<prefix>[""']?)(?<key>password|passwd|pwd|token|secret|api[_-]?key|access[_-]?key|client[_-]?secret)(?<suffix>[""']?)(?<separator>\s*[:=]\s*)(?<value>""[^""]*""|'[^']*'|[^;\s,}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKeyValuePattern();

    [GeneratedRegex(
        @"\b(?<scheme>bearer)\s+(?<token>[A-Za-z0-9._~+/=-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderPattern();

    [GeneratedRegex(
        @"\b(?<key>AccessKey|SharedAccessKey|AccountKey)\s*=\s*(?<value>[^;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringSecretPattern();

    [GeneratedRegex(
        @"\b(?<scheme>https?|wss?)://(?<userinfo>[^/\s@]+)@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialedUriPattern();

    [GeneratedRegex(
        @"(?<prefix>[?&](?:sig|token|code|access_token|client_secret|api[_-]?key|apikey|password|secret)=)(?<value>[^&#\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQueryValuePattern();

    [GeneratedRegex(
        @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtTokenPattern();

    [GeneratedRegex(
        @"\b(?:gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenPattern();

    [GeneratedRegex(
        @"^(?:password|passwd|pwd|token|secret|api[_-]?key|access[_-]?key|client[_-]?secret|authorization|accesskey|sharedaccesskey|accountkey|sig|code|access_token|client_secret|apikey)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitivePropertyNamePattern();
}
