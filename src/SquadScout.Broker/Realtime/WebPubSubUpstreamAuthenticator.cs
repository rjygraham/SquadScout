using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SquadScout.Broker.Configuration;

namespace SquadScout.Broker.Realtime;

public sealed class WebPubSubUpstreamAuthenticator
{
    public const string WebHookSignatureHeaderName = "WebHook-Signature";
    public const string CloudEventSignatureHeaderName = "ce-signature";

    private const string ClientPrincipalHeaderName = "x-ms-client-principal";
    private const string ClientPrincipalIdHeaderName = "x-ms-client-principal-id";
    private const string WebsiteInstanceIdEnvironmentVariable = "WEBSITE_INSTANCE_ID";
    private static readonly JsonSerializerOptions PrincipalOptions = new(JsonSerializerDefaults.Web);

    private readonly string[] _trustedPrincipalIds;
    private readonly string[] _upstreamAccessKeys;
    private readonly string _webPubSubHost;
    private readonly ILogger<WebPubSubUpstreamAuthenticator> _logger;

    public WebPubSubUpstreamAuthenticator(
        IOptions<AzureWebPubSubOptions> options,
        ILogger<WebPubSubUpstreamAuthenticator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var configuredOptions = options.Value;
        _webPubSubHost = ResolveHost(configuredOptions.ConnectionString);
        _upstreamAccessKeys = ResolveConnectionStringValues(configuredOptions.ConnectionString, "AccessKey");
        _trustedPrincipalIds = configuredOptions.TrustedUpstreamPrincipalIds
            .Where(principalId => !string.IsNullOrWhiteSpace(principalId))
            .Select(principalId => principalId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool TryAuthenticate(IHeaderDictionary headers, out string failureMessage)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!HasExpectedOrigin(headers, out var originError))
        {
            failureMessage = originError;
            return false;
        }

        string? managedIdentityError = null;
        if (TryAuthenticateManagedIdentity(headers, out var managedIdentityAttempted, out managedIdentityError))
        {
            failureMessage = string.Empty;
            return true;
        }

        if (TryAuthenticateSignature(headers, out var signatureError))
        {
            failureMessage = string.Empty;
            return true;
        }

        failureMessage = managedIdentityAttempted
            ? $"{managedIdentityError} {signatureError}".Trim()
            : signatureError;
        return false;
    }

    private bool HasExpectedOrigin(IHeaderDictionary headers, out string failureMessage)
    {
        if (string.IsNullOrWhiteSpace(_webPubSubHost))
        {
            failureMessage =
                "Azure Web PubSub upstream validation requires AzureWebPubSub:ConnectionString to include a valid Endpoint.";
            return false;
        }

        var originHosts = GetHeaderList(headers, WebPubSubUpstreamHandler.WebHookRequestOriginHeaderName);
        if (originHosts.Count == 0)
        {
            failureMessage = "Azure Web PubSub upstream requests must include the WebHook-Request-Origin header.";
            return false;
        }

        if (originHosts.Contains(_webPubSubHost, StringComparer.OrdinalIgnoreCase))
        {
            failureMessage = string.Empty;
            return true;
        }

        failureMessage = $"Azure Web PubSub upstream requests must originate from {_webPubSubHost}.";
        return false;
    }

    private bool TryAuthenticateManagedIdentity(
        IHeaderDictionary headers,
        out bool attempted,
        out string failureMessage)
    {
        attempted = HasEasyAuthHeaders(headers);
        if (!attempted)
        {
            failureMessage = string.Empty;
            return false;
        }

        if (!CanTrustEasyAuthBoundary())
        {
            failureMessage = "Easy Auth headers are only trusted inside the broker host boundary.";
            return false;
        }

        if (_trustedPrincipalIds.Length == 0)
        {
            failureMessage =
                "Managed identity upstream validation requires AzureWebPubSub:TrustedUpstreamPrincipalIds to be configured.";
            return false;
        }

        if (!TryResolvePrincipalId(headers, out var principalId, out failureMessage))
        {
            return false;
        }

        if (_trustedPrincipalIds.Contains(principalId, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Accepted Azure Web PubSub upstream request from trusted Easy Auth principal {PrincipalId}.",
                principalId);
            failureMessage = string.Empty;
            return true;
        }

        failureMessage = "The authenticated upstream principal is not authorized for Azure Web PubSub requests.";
        return false;
    }

    private bool TryAuthenticateSignature(IHeaderDictionary headers, out string failureMessage)
    {
        if (_upstreamAccessKeys.Length == 0)
        {
            failureMessage =
                "Signature validation requires AzureWebPubSub:ConnectionString to include an AccessKey.";
            return false;
        }

        var signatures = GetHeaderList(headers, CloudEventSignatureHeaderName, WebHookSignatureHeaderName);
        if (signatures.Count == 0)
        {
            failureMessage = "Azure Web PubSub upstream requests must include a signature header.";
            return false;
        }

        var connectionId = GetHeaderValue(headers, WebPubSubUpstreamHandler.CloudEventConnectionIdHeaderName);
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            failureMessage = "Azure Web PubSub upstream requests must include the ce-connectionId header.";
            return false;
        }

        foreach (var accessKey in _upstreamAccessKeys)
        {
            var expectedSignature = ComputeSignature(accessKey, connectionId.Trim());
            if (signatures.Any(signature => FixedTimeEquals(signature, expectedSignature)))
            {
                _logger.LogDebug(
                    "Accepted Azure Web PubSub upstream request for connection {ConnectionId} after signature validation.",
                    connectionId);
                failureMessage = string.Empty;
                return true;
            }
        }

        failureMessage = "Azure Web PubSub upstream signature validation failed.";
        return false;
    }

    private static bool TryResolvePrincipalId(
        IHeaderDictionary headers,
        out string principalId,
        out string failureMessage)
    {
        var headerPrincipalId = GetHeaderValue(headers, ClientPrincipalIdHeaderName)?.Trim();
        if (!TryReadPrincipalPayload(headers, out var payloadPrincipalId, out failureMessage))
        {
            principalId = string.Empty;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(headerPrincipalId) &&
            !string.IsNullOrWhiteSpace(payloadPrincipalId) &&
            !string.Equals(headerPrincipalId, payloadPrincipalId, StringComparison.OrdinalIgnoreCase))
        {
            principalId = string.Empty;
            failureMessage = "Easy Auth principal headers did not match the authenticated principal payload.";
            return false;
        }

        principalId = headerPrincipalId ?? payloadPrincipalId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(principalId))
        {
            failureMessage = "The authenticated upstream principal did not include a principal identifier.";
            return false;
        }

        if (!IsSafeTrustedPrincipal(principalId))
        {
            failureMessage = "The authenticated upstream principal identifier contains unsupported characters.";
            return false;
        }

        failureMessage = string.Empty;
        return true;
    }

    private static bool TryReadPrincipalPayload(
        IHeaderDictionary headers,
        out string? principalId,
        out string failureMessage)
    {
        principalId = null;
        var principalHeader = GetHeaderValue(headers, ClientPrincipalHeaderName);
        if (string.IsNullOrWhiteSpace(principalHeader))
        {
            failureMessage = string.Empty;
            return true;
        }

        try
        {
            var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(NormalizeBase64(principalHeader)));
            var payload = JsonSerializer.Deserialize<ClientPrincipalPayload>(payloadJson, PrincipalOptions);
            principalId = payload?.UserId
                ?? GetClaimValue(payload?.Claims, "http://schemas.microsoft.com/identity/claims/objectidentifier")
                ?? GetClaimValue(payload?.Claims, "oid")
                ?? GetClaimValue(payload?.Claims, "sub");
            failureMessage = string.Empty;
            return true;
        }
        catch (FormatException)
        {
            failureMessage = "The Easy Auth principal payload was not valid base64.";
            return false;
        }
        catch (JsonException)
        {
            failureMessage = "The Easy Auth principal payload was not valid JSON.";
            return false;
        }
    }

    private static string ResolveHost(string? connectionString)
    {
        var endpoint = ResolveConnectionStringValues(connectionString, "Endpoint").FirstOrDefault();
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            ? endpointUri.Host
            : string.Empty;
    }

    private static string[] ResolveConnectionStringValues(string? connectionString, string key)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return [];
        }

        return connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var separatorIndex = part.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    return (Key: string.Empty, Value: string.Empty);
                }

                return (
                    Key: part[..separatorIndex].Trim(),
                    Value: part[(separatorIndex + 1)..].Trim());
            })
            .Where(part => string.Equals(part.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(part.Value))
            .Select(part => part.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? GetClaimValue(IReadOnlyCollection<ClientPrincipalClaim>? claims, string type) =>
        claims?.FirstOrDefault(claim => claim.Type.Equals(type, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string ComputeSignature(string accessKey, string connectionId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(accessKey));
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(connectionId));
        return $"sha256={Convert.ToHexString(hashBytes)}";
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left.ToUpperInvariant());
        var rightBytes = Encoding.ASCII.GetBytes(right.ToUpperInvariant());
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool HasEasyAuthHeaders(IHeaderDictionary headers) =>
        headers.ContainsKey(ClientPrincipalIdHeaderName) ||
        headers.ContainsKey(ClientPrincipalHeaderName);

    private static bool CanTrustEasyAuthBoundary() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(WebsiteInstanceIdEnvironmentVariable));

    private static bool IsSafeTrustedPrincipal(string principalId) =>
        principalId.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-');

    private static string NormalizeBase64(string value)
    {
        var remainder = value.Length % 4;
        return remainder == 0 ? value : value.PadRight(value.Length + (4 - remainder), '=');
    }

    private static string? GetHeaderValue(IHeaderDictionary headers, string headerName) =>
        headers.TryGetValue(headerName, out var values)
            ? values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : null;

    private static IReadOnlyList<string> GetHeaderList(IHeaderDictionary headers, params string[] headerNames)
    {
        var values = new List<string>();
        foreach (var headerName in headerNames)
        {
            if (!headers.TryGetValue(headerName, out var headerValues))
            {
                continue;
            }

            foreach (var headerValue in headerValues)
            {
                values.AddRange(
                    (headerValue ?? string.Empty)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
            }
        }

        return values;
    }

    private sealed record ClientPrincipalPayload
    {
        [JsonPropertyName("userId")]
        public string? UserId { get; init; }

        public IReadOnlyCollection<ClientPrincipalClaim> Claims { get; init; } = [];
    }

    private sealed record ClientPrincipalClaim
    {
        [JsonPropertyName("typ")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("val")]
        public string Value { get; init; } = string.Empty;
    }
}
