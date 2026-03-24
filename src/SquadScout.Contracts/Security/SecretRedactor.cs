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
            match => $"{match.Groups["key"].Value}{match.Groups["separator"].Value}{RedactedValue}");

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

    [GeneratedRegex(
        @"\b(?<key>password|passwd|pwd|token|secret|api[_-]?key|access[_-]?key|client[_-]?secret)\b(?<separator>\s*[:=]\s*)(?<value>""[^""]*""|'[^']*'|[^;\s,]+)",
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
}
