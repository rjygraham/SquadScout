using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Options;
using SquadScout.Contracts.Realtime;
using SquadScout.Functions.Configuration;
using SquadScout.Functions.Negotiation;

namespace SquadScout.Broker.Tests;

public sealed class PubSubNegotiateEndpointTests
{
    private const string WebsiteInstanceIdEnvironmentVariable = "WEBSITE_INSTANCE_ID";
    private static readonly object EnvironmentVariableSync = new();

    [Fact]
    public void SessionGroupNameBuildsBaseAndBrokerScopedGroups()
    {
        var baseGroup = SessionGroupName.Create("proj-01", "session-abc");
        var brokerScopedGroup = SessionGroupName.Create("proj-01", "session-abc", "broker-west");

        Assert.Equal("session:proj-01:session-abc", baseGroup);
        Assert.Equal("session:proj-01:session-abc:broker-west", brokerScopedGroup);
    }

    [Fact]
    public void SessionGroupNameRejectsUnsafeSegments()
    {
        var valid = SessionGroupName.TryCreate("proj:01", "session-abc", null, out _, out var validationError);

        Assert.False(valid);
        Assert.Contains("projectId", validationError, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityResolverUsesEasyAuthHeadersWhenAvailable()
    {
        var resolver = CreateIdentityResolver(new FunctionsHostOptions());
        var headers = new HttpHeadersCollection
        {
            { "x-ms-client-principal-id", "entra-user-42" },
            { "x-ms-client-principal-name", "Ryan Graham" },
            { "x-ms-client-principal-idp", "aad" },
            { "x-ms-client-principal", CreatePrincipalPayload("entra-user-42", "aad") }
        };

        var (success, identity, statusCode, failureMessage) = ResolveInTrustedFunctionBoundary(
            resolver,
            new Uri("https://squadscout.azurewebsites.net/api/negotiate"),
            headers);

        Assert.True(success);
        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Equal(string.Empty, failureMessage);
        Assert.Equal("entra-user-42", identity.PrincipalId);
        Assert.Equal("Ryan Graham", identity.DisplayName);
        Assert.Equal("aad", identity.IdentityProvider);
        Assert.False(identity.IsDevelopment);
    }

    [Fact]
    public void IdentityResolverRejectsEasyAuthHeadersOutsideTrustedFunctionBoundary()
    {
        var resolver = CreateIdentityResolver(new FunctionsHostOptions());
        var headers = new HttpHeadersCollection
        {
            { "x-ms-client-principal-id", "entra-user-42" },
            { "x-ms-client-principal-idp", "aad" }
        };

        var success = resolver.TryResolve(
            new Uri("http://localhost:7071/api/negotiate"),
            headers,
            out _,
            out var statusCode,
            out var failureMessage);

        Assert.False(success);
        Assert.Equal(HttpStatusCode.Unauthorized, statusCode);
        Assert.Contains("only trusted", failureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IdentityResolverAllowsDevelopmentIdentityOnlyOnLoopback()
    {
        var resolver = CreateIdentityResolver(new FunctionsHostOptions
        {
            EnableLocalDevelopmentIdentity = true,
            DevelopmentUserId = "local-dev",
            DevelopmentUserDisplayName = "Local Developer"
        });
        var headers = new HttpHeadersCollection
        {
            { NegotiationIdentityResolver.DevelopmentUserHeaderName, "ryan-dev" },
            { NegotiationIdentityResolver.DevelopmentDisplayNameHeaderName, "Ryan Dev" }
        };

        var success = resolver.TryResolve(
            new Uri("http://localhost:7071/api/negotiate"),
            headers,
            out var identity,
            out var statusCode,
            out var failureMessage);

        Assert.True(success);
        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.Equal(string.Empty, failureMessage);
        Assert.Equal("ryan-dev", identity.PrincipalId);
        Assert.Equal("Ryan Dev", identity.DisplayName);
        Assert.True(identity.IsDevelopment);
    }

    [Fact]
    public async Task NegotiationServiceIssuesScopedRolesAndRefreshWindow()
    {
        var accessUriClient = new RecordingAccessUriClient();
        var issuedAt = new DateTimeOffset(2026, 03, 25, 13, 00, 00, TimeSpan.Zero);
        var service = new WebPubSubNegotiationService(
            accessUriClient,
            Options.Create(new FunctionsHostOptions
            {
                WebPubSubEndpoint = "https://squadscout.webpubsub.azure.com",
                WebPubSubHub = "squadscout",
                TokenLifetimeMinutes = 60
            }),
            new FixedTimeProvider(issuedAt));

        var response = await service.NegotiateAsync(
            new PubSubNegotiateRequest
            {
                ProjectId = "proj-01",
                SessionId = "session-abc",
                ParticipantKind = PubSubParticipantKind.Broker,
                BrokerId = "broker-west"
            },
            new NegotiationIdentity
            {
                PrincipalId = "entra-user-42",
                DisplayName = "Ryan Graham",
                IdentityProvider = "aad"
            },
            CancellationToken.None);

        Assert.Equal("session:proj-01:session-abc:broker-west", response.SessionGroup);
        Assert.Equal("broker:proj-01:session-abc:broker-west:entra-user-42", response.UserId);
        Assert.Equal(response.SessionGroup, Assert.Single(accessUriClient.Groups));
        Assert.Equal(
            new[]
            {
                "webpubsub.joinLeaveGroup.session:proj-01:session-abc:broker-west",
                "webpubsub.sendToGroup.session:proj-01:session-abc:broker-west"
            },
            accessUriClient.Roles);
        Assert.Equal(new Uri("wss://example.webpubsub.azure.com/client/hubs/squadscout?access_token=test").ToString(), response.Url);
        Assert.Equal(issuedAt.AddMinutes(60), response.ExpiresAtUtc);
        Assert.Equal(issuedAt.AddMinutes(50), response.RefreshAtUtc);
        Assert.Equal(response.ExpiresAtUtc, accessUriClient.ExpiresAtUtc);
        Assert.Equal(response.UserId, accessUriClient.UserId);
    }

    [Fact]
    public async Task BrokerNegotiationWithoutBrokerIdFallsBackToSessionWideGroup()
    {
        var accessUriClient = new RecordingAccessUriClient();
        var service = new WebPubSubNegotiationService(
            accessUriClient,
            Options.Create(new FunctionsHostOptions
            {
                WebPubSubEndpoint = "https://squadscout.webpubsub.azure.com",
                WebPubSubHub = "squadscout",
                TokenLifetimeMinutes = 60
            }),
            new FixedTimeProvider(new DateTimeOffset(2026, 03, 25, 13, 00, 00, TimeSpan.Zero)));

        var response = await service.NegotiateAsync(
            new PubSubNegotiateRequest
            {
                ProjectId = "proj-01",
                SessionId = "session-abc",
                ParticipantKind = PubSubParticipantKind.Broker
            },
            new NegotiationIdentity
            {
                PrincipalId = "entra-user-42",
                DisplayName = "Ryan Graham",
                IdentityProvider = "aad"
            },
            CancellationToken.None);

        Assert.Equal("session:proj-01:session-abc", response.SessionGroup);
        Assert.Equal("broker:proj-01:session-abc:entra-user-42", response.UserId);
        Assert.Equal(new[] { response.SessionGroup }, accessUriClient.Groups);
        Assert.Equal(
            new[]
            {
                "webpubsub.joinLeaveGroup.session:proj-01:session-abc",
                "webpubsub.sendToGroup.session:proj-01:session-abc"
            },
            accessUriClient.Roles);
    }

    [Fact]
    public async Task ShortLivedTokensStillLeaveUsableTimeBeforeRefresh()
    {
        var accessUriClient = new RecordingAccessUriClient();
        var issuedAt = new DateTimeOffset(2026, 03, 25, 13, 00, 00, TimeSpan.Zero);
        var service = new WebPubSubNegotiationService(
            accessUriClient,
            Options.Create(new FunctionsHostOptions
            {
                WebPubSubEndpoint = "https://squadscout.webpubsub.azure.com",
                WebPubSubHub = "squadscout",
                TokenLifetimeMinutes = 1
            }),
            new FixedTimeProvider(issuedAt));

        var response = await service.NegotiateAsync(
            new PubSubNegotiateRequest
            {
                ProjectId = "proj-01",
                SessionId = "session-abc",
                ParticipantKind = PubSubParticipantKind.Client
            },
            new NegotiationIdentity
            {
                PrincipalId = "entra-user-42",
                DisplayName = "Ryan Graham",
                IdentityProvider = "aad"
            },
            CancellationToken.None);

        Assert.Equal(issuedAt.AddMinutes(1), response.ExpiresAtUtc);
        Assert.Equal(issuedAt.AddSeconds(30), response.RefreshAtUtc);
        Assert.Equal("client:proj-01:session-abc:entra-user-42", response.UserId);
    }

    [Fact]
    public void IdentityResolverRejectsRemoteDevelopmentBypass()
    {
        var resolver = CreateIdentityResolver(new FunctionsHostOptions
        {
            EnableLocalDevelopmentIdentity = true
        });

        var success = resolver.TryResolve(
            new Uri("https://squadscout.azurewebsites.net/api/negotiate"),
            new HttpHeadersCollection(),
            out _,
            out var statusCode,
            out var failureMessage);

        Assert.False(success);
        Assert.Equal(HttpStatusCode.Unauthorized, statusCode);
        Assert.Contains("localhost", failureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IdentityResolverRejectsMismatchedEasyAuthPayload()
    {
        var resolver = CreateIdentityResolver(new FunctionsHostOptions());
        var headers = new HttpHeadersCollection
        {
            { "x-ms-client-principal-id", "entra-user-42" },
            { "x-ms-client-principal-idp", "aad" },
            { "x-ms-client-principal", CreatePrincipalPayload("different-user", "aad") }
        };

        var (success, _, statusCode, failureMessage) = ResolveInTrustedFunctionBoundary(
            resolver,
            new Uri("https://squadscout.azurewebsites.net/api/negotiate"),
            headers);

        Assert.False(success);
        Assert.Equal(HttpStatusCode.Unauthorized, statusCode);
        Assert.Contains("did not match", failureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IdentityResolverRejectsUnsafePrincipalIdentifiers()
    {
        var resolver = CreateIdentityResolver(new FunctionsHostOptions());
        var headers = new HttpHeadersCollection
        {
            { "x-ms-client-principal-id", "entra:user:42" },
            { "x-ms-client-principal-idp", "aad" }
        };

        var (success, _, statusCode, failureMessage) = ResolveInTrustedFunctionBoundary(
            resolver,
            new Uri("https://squadscout.azurewebsites.net/api/negotiate"),
            headers);

        Assert.False(success);
        Assert.Equal(HttpStatusCode.Unauthorized, statusCode);
        Assert.Contains("principalId", failureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void NegotiateRequestValidatorRejectsBrokerScopedClientRequests()
    {
        var valid = PubSubNegotiateRequestValidator.TryValidate(
            new PubSubNegotiateRequest
            {
                ProjectId = "proj-01",
                SessionId = "session-abc",
                ParticipantKind = PubSubParticipantKind.Client,
                BrokerId = "broker-west"
            },
            out var validationError);

        Assert.False(valid);
        Assert.Contains("brokerId", validationError, StringComparison.Ordinal);
    }

    [Fact]
    public void NegotiateRequestValidatorRejectsUnknownParticipantKinds()
    {
        var valid = PubSubNegotiateRequestValidator.TryValidate(
            new PubSubNegotiateRequest
            {
                ProjectId = "proj-01",
                SessionId = "session-abc",
                ParticipantKind = (PubSubParticipantKind)999
            },
            out var validationError);

        Assert.False(valid);
        Assert.Contains("participantKind", validationError, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionUserIdRejectsUnknownParticipantKinds()
    {
        var identity = new NegotiationIdentity
        {
            PrincipalId = "entra-user-42",
            IdentityProvider = "aad"
        };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            identity.CreateConnectionUserId((PubSubParticipantKind)999, "proj-01", "session-abc"));

        Assert.Contains("participantKind", exception.Message, StringComparison.Ordinal);
    }

    private static NegotiationIdentityResolver CreateIdentityResolver(FunctionsHostOptions options) =>
        new(Options.Create(options));

    private static (bool Success, NegotiationIdentity Identity, HttpStatusCode StatusCode, string FailureMessage)
        ResolveInTrustedFunctionBoundary(
            NegotiationIdentityResolver resolver,
            Uri requestUri,
            HttpHeadersCollection headers) =>
        WithEnvironmentVariable(
            WebsiteInstanceIdEnvironmentVariable,
            "trusted-boundary",
            () =>
            {
                var success = resolver.TryResolve(
                    requestUri,
                    headers,
                    out var identity,
                    out var statusCode,
                    out var failureMessage);

                return (success, identity, statusCode, failureMessage);
            });

    private static T WithEnvironmentVariable<T>(string name, string? value, Func<T> action)
    {
        lock (EnvironmentVariableSync)
        {
            var original = Environment.GetEnvironmentVariable(name);
            try
            {
                Environment.SetEnvironmentVariable(name, value);
                return action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, original);
            }
        }
    }

    private static string CreatePrincipalPayload(string principalId, string identityProvider)
    {
        var payload = new
        {
            auth_typ = identityProvider,
            userId = principalId,
            claims = new[]
            {
                new
                {
                    typ = "http://schemas.microsoft.com/identity/claims/objectidentifier",
                    val = principalId
                }
            }
        };

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
    }

    private sealed class RecordingAccessUriClient : IWebPubSubAccessUriClient
    {
        public DateTimeOffset ExpiresAtUtc { get; private set; }

        public string UserId { get; private set; } = string.Empty;

        public string[] Roles { get; private set; } = [];

        public string[] Groups { get; private set; } = [];

        public Task<Uri> GetClientAccessUriAsync(
            DateTimeOffset expiresAt,
            string userId,
            IEnumerable<string> roles,
            IEnumerable<string> groups,
            CancellationToken cancellationToken)
        {
            ExpiresAtUtc = expiresAt;
            UserId = userId;
            Roles = [.. roles];
            Groups = [.. groups];

            return Task.FromResult(new Uri("wss://example.webpubsub.azure.com/client/hubs/squadscout?access_token=test"));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
