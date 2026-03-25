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
        if (HasEasyAuthHeaders(headers))
        {
            if (!CanTrustEasyAuthBoundary())
            {
                identity = new NegotiationIdentity();
                failureStatus = HttpStatusCode.Unauthorized;
                failureMessage = "Easy Auth identity headers are only trusted inside the Azure Functions host boundary. Use the localhost development identity when running locally.";
                return false;
            }

            if (TryResolveEasyAuthIdentity(headers, out identity, out failureMessage))
            {
                failureStatus = HttpStatusCode.OK;
                failureMessage = string.Empty;
                return true;
            }

            failureStatus = HttpStatusCode.Unauthorized;
            return false;
        }

        if (CanUseLocalDevelopmentIdentity(requestUri))
        {
            identity = ResolveLocalDevelopmentIdentity(headers);
            if (!NegotiationIdentity.TryValidateTrustedIdentity(identity.PrincipalId, identity.IdentityProvider, out failureMessage))
            {
                identity = new NegotiationIdentity();
                failureStatus = HttpStatusCode.Unauthorized;
                return false;
            }

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

        return requestUri.IsLoopback;
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

    private static bool TryResolveEasyAuthIdentity(
        HttpHeadersCollection headers,
        out NegotiationIdentity identity,
        out string validationError)
    {
        var principalId = GetHeaderValue(headers, ClientPrincipalIdHeaderName);
        var displayName = GetHeaderValue(headers, ClientPrincipalNameHeaderName);
        var identityProvider = GetHeaderValue(headers, ClientPrincipalIdentityProviderHeaderName);
        var principalHeader = GetHeaderValue(headers, ClientPrincipalHeaderName);
        NegotiationIdentity? payloadIdentity = null;

        if (!string.IsNullOrWhiteSpace(principalHeader) &&
            !TryReadPrincipalPayload(principalHeader, out payloadIdentity, out validationError))
        {
            identity = new NegotiationIdentity();
            return false;
        }

        if (string.IsNullOrWhiteSpace(principalId) && payloadIdentity is null)
        {
            identity = new NegotiationIdentity();
            validationError = "The Easy Auth identity boundary did not include a principal identifier.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(principalId))
        {
            identity = payloadIdentity!;
            return NegotiationIdentity.TryValidateTrustedIdentity(identity.PrincipalId, identity.IdentityProvider, out validationError);
        }

        identity = new NegotiationIdentity
        {
            PrincipalId = principalId.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? principalId.Trim() : displayName.Trim(),
            IdentityProvider = string.IsNullOrWhiteSpace(identityProvider)
                ? payloadIdentity?.IdentityProvider ?? "aad"
                : identityProvider.Trim(),
            IsDevelopment = false
        };

        if (!NegotiationIdentity.TryValidateTrustedIdentity(identity.PrincipalId, identity.IdentityProvider, out validationError))
        {
            identity = new NegotiationIdentity();
            return false;
        }

        if (payloadIdentity is not null && !identity.MatchesAuthenticatedPrincipal(payloadIdentity))
        {
            identity = new NegotiationIdentity();
            validationError = "Easy Auth identity headers did not match the authenticated principal payload.";
            return false;
        }

        validationError = string.Empty;
        return true;
    }

    private static bool TryReadPrincipalPayload(
        string principalHeader,
        out NegotiationIdentity identity,
        out string validationError)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(NormalizeBase64(principalHeader)));
            var payload = JsonSerializer.Deserialize<ClientPrincipalPayload>(json, PrincipalOptions);
            if (payload is null)
            {
                identity = new NegotiationIdentity();
                validationError = "The Easy Auth principal payload was missing required claims.";
                return false;
            }

            var principalId = payload.UserId
                ?? GetClaimValue(payload.Claims, "http://schemas.microsoft.com/identity/claims/objectidentifier")
                ?? GetClaimValue(payload.Claims, "oid")
                ?? GetClaimValue(payload.Claims, "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")
                ?? GetClaimValue(payload.Claims, "sub");

            if (string.IsNullOrWhiteSpace(principalId))
            {
                identity = new NegotiationIdentity();
                validationError = "The Easy Auth principal payload did not include a supported Entra principal identifier.";
                return false;
            }

            identity = new NegotiationIdentity
            {
                PrincipalId = principalId.Trim(),
                DisplayName = GetClaimValue(payload.Claims, "name")
                    ?? GetClaimValue(payload.Claims, "preferred_username")
                    ?? principalId.Trim(),
                IdentityProvider = payload.IdentityProvider
                    ?? payload.AuthenticationType
                    ?? GetClaimValue(payload.Claims, "http://schemas.microsoft.com/identity/claims/identityprovider")
                    ?? "aad",
                IsDevelopment = false
            };

            return NegotiationIdentity.TryValidateTrustedIdentity(identity.PrincipalId, identity.IdentityProvider, out validationError);
        }
        catch (FormatException)
        {
            identity = new NegotiationIdentity();
            validationError = "The Easy Auth principal payload was not valid base64.";
            return false;
        }
        catch (JsonException)
        {
            identity = new NegotiationIdentity();
            validationError = "The Easy Auth principal payload was not valid JSON.";
            return false;
        }
    }

    private static bool HasEasyAuthHeaders(HttpHeadersCollection headers) =>
        headers.Contains(ClientPrincipalHeaderName) ||
        headers.Contains(ClientPrincipalIdHeaderName) ||
        headers.Contains(ClientPrincipalNameHeaderName) ||
        headers.Contains(ClientPrincipalIdentityProviderHeaderName);

    private static bool CanTrustEasyAuthBoundary() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"));

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
        [JsonPropertyName("auth_typ")]
        public string? AuthenticationType { get; init; }

        [JsonPropertyName("identityProvider")]
        public string? IdentityProvider { get; init; }

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
