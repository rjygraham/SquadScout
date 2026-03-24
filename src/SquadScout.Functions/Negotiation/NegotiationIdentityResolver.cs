using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Options;
using SquadScout.Functions.Configuration;

namespace SquadScout.Functions.Negotiation;

public sealed class NegotiationIdentityResolver
{
    public const string DevelopmentUserHeaderName = "x-squadscout-dev-user";
    public const string DevelopmentDisplayNameHeaderName = "x-squadscout-dev-name";

    private const string ClientPrincipalHeaderName = "x-ms-client-principal";
    private const string ClientPrincipalIdHeaderName = "x-ms-client-principal-id";
    private const string ClientPrincipalNameHeaderName = "x-ms-client-principal-name";
    private const string ClientPrincipalIdentityProviderHeaderName = "x-ms-client-principal-idp";

    private static readonly JsonSerializerOptions PrincipalOptions = new(JsonSerializerDefaults.Web);
    private readonly FunctionsHostOptions _options;

    public NegotiationIdentityResolver(IOptions<FunctionsHostOptions> options)
    {
        _options = options.Value;
    }

    public bool TryResolve(
        Uri requestUri,
        HttpHeadersCollection headers,
        out NegotiationIdentity identity,
        out HttpStatusCode failureStatus,
        out string failureMessage)
    {
        if (TryResolveEasyAuthIdentity(headers, out identity))
        {
            failureStatus = HttpStatusCode.OK;
            failureMessage = string.Empty;
            return true;
        }

        if (CanUseLocalDevelopmentIdentity(requestUri))
        {
            identity = ResolveLocalDevelopmentIdentity(headers);
            failureStatus = HttpStatusCode.OK;
            failureMessage = string.Empty;
            return true;
        }

        identity = new NegotiationIdentity();
        failureStatus = HttpStatusCode.Unauthorized;
        failureMessage = "The negotiate endpoint requires an authenticated Easy Auth principal or an enabled localhost development identity.";
        return false;
    }

    private bool CanUseLocalDevelopmentIdentity(Uri requestUri)
    {
        if (!_options.EnableLocalDevelopmentIdentity)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")))
        {
            return false;
        }

        return requestUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               requestUri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               requestUri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase) ||
               requestUri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private NegotiationIdentity ResolveLocalDevelopmentIdentity(HttpHeadersCollection headers)
    {
        var principalId = GetHeaderValue(headers, DevelopmentUserHeaderName) ?? _options.DevelopmentUserId;
        var displayName = GetHeaderValue(headers, DevelopmentDisplayNameHeaderName) ?? _options.DevelopmentUserDisplayName;

        return new NegotiationIdentity
        {
            PrincipalId = principalId,
            DisplayName = displayName,
            IdentityProvider = _options.DevelopmentIdentityProvider,
            IsDevelopment = true
        };
    }

    private static bool TryResolveEasyAuthIdentity(HttpHeadersCollection headers, out NegotiationIdentity identity)
    {
        var principalId = GetHeaderValue(headers, ClientPrincipalIdHeaderName);
        var displayName = GetHeaderValue(headers, ClientPrincipalNameHeaderName);
        var identityProvider = GetHeaderValue(headers, ClientPrincipalIdentityProviderHeaderName);

        if (string.IsNullOrWhiteSpace(principalId) &&
            TryReadPrincipalPayload(headers, out var payloadIdentity))
        {
            identity = payloadIdentity;
            return true;
        }

        if (string.IsNullOrWhiteSpace(principalId))
        {
            identity = new NegotiationIdentity();
            return false;
        }

        identity = new NegotiationIdentity
        {
            PrincipalId = principalId,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? principalId : displayName,
            IdentityProvider = string.IsNullOrWhiteSpace(identityProvider) ? "aad" : identityProvider,
            IsDevelopment = false
        };

        return true;
    }

    private static bool TryReadPrincipalPayload(HttpHeadersCollection headers, out NegotiationIdentity identity)
    {
        var principalHeader = GetHeaderValue(headers, ClientPrincipalHeaderName);
        if (string.IsNullOrWhiteSpace(principalHeader))
        {
            identity = new NegotiationIdentity();
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(NormalizeBase64(principalHeader)));
            var payload = JsonSerializer.Deserialize<ClientPrincipalPayload>(json, PrincipalOptions);
            if (payload?.Claims is null)
            {
                identity = new NegotiationIdentity();
                return false;
            }

            var principalId = GetClaimValue(payload.Claims, "http://schemas.microsoft.com/identity/claims/objectidentifier")
                ?? GetClaimValue(payload.Claims, "oid")
                ?? GetClaimValue(payload.Claims, "sub");

            if (string.IsNullOrWhiteSpace(principalId))
            {
                identity = new NegotiationIdentity();
                return false;
            }

            identity = new NegotiationIdentity
            {
                PrincipalId = principalId,
                DisplayName = GetClaimValue(payload.Claims, "name")
                    ?? GetClaimValue(payload.Claims, "preferred_username")
                    ?? principalId,
                IdentityProvider = GetClaimValue(payload.Claims, "http://schemas.microsoft.com/identity/claims/identityprovider")
                    ?? "aad",
                IsDevelopment = false
            };

            return true;
        }
        catch (FormatException)
        {
            identity = new NegotiationIdentity();
            return false;
        }
        catch (JsonException)
        {
            identity = new NegotiationIdentity();
            return false;
        }
    }

    private static string? GetClaimValue(IReadOnlyCollection<ClientPrincipalClaim> claims, string type) =>
        claims.FirstOrDefault(claim =>
            claim.Type.Equals(type, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? GetHeaderValue(HttpHeadersCollection headers, string name)
    {
        if (!headers.TryGetValues(name, out var values))
        {
            return null;
        }

        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string NormalizeBase64(string value)
    {
        var remainder = value.Length % 4;
        return remainder == 0 ? value : value.PadRight(value.Length + (4 - remainder), '=');
    }

    private sealed record ClientPrincipalPayload
    {
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
