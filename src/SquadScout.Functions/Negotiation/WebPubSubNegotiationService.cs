using Microsoft.Extensions.Options;
using SquadScout.Contracts.Realtime;
using SquadScout.Functions.Configuration;

namespace SquadScout.Functions.Negotiation;

public sealed class WebPubSubNegotiationService
{
    private readonly IWebPubSubAccessUriClient _accessUriClient;
    private readonly FunctionsHostOptions _options;
    private readonly TimeProvider _timeProvider;

    public WebPubSubNegotiationService(
        IWebPubSubAccessUriClient accessUriClient,
        IOptions<FunctionsHostOptions> options,
        TimeProvider timeProvider)
    {
        _accessUriClient = accessUriClient;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task<PubSubNegotiateResponse> NegotiateAsync(
        PubSubNegotiateRequest request,
        NegotiationIdentity identity,
        CancellationToken cancellationToken)
    {
        var sessionGroup = SessionGroupName.Create(request.ProjectId, request.SessionId, request.BrokerId);
        var issuedAt = _timeProvider.GetUtcNow();
        var expiresAt = issuedAt.AddMinutes(_options.TokenLifetimeMinutes);
        var refreshAt = expiresAt - CalculateRefreshLeadTime(TimeSpan.FromMinutes(_options.TokenLifetimeMinutes));
        var roles = CreateScopedRoles(sessionGroup);
        var autoJoinGroups = new[] { sessionGroup };
        var userId = identity.CreateConnectionUserId(request.ParticipantKind, request.BrokerId);
        var accessUri = await _accessUriClient.GetClientAccessUriAsync(
            expiresAt,
            userId,
            roles,
            autoJoinGroups,
            cancellationToken);

        return new PubSubNegotiateResponse
        {
            Url = accessUri.ToString(),
            Hub = _options.WebPubSubHub,
            UserId = userId,
            ProjectId = request.ProjectId,
            SessionId = request.SessionId,
            ParticipantKind = request.ParticipantKind,
            BrokerId = request.BrokerId,
            SessionGroup = sessionGroup,
            PrincipalId = identity.PrincipalId,
            DisplayName = identity.DisplayName,
            IdentityProvider = identity.IdentityProvider,
            IsDevelopmentIdentity = identity.IsDevelopment,
            Roles = roles,
            AutoJoinGroups = autoJoinGroups,
            ExpiresAtUtc = expiresAt,
            RefreshAtUtc = refreshAt
        };
    }

    public static string[] CreateScopedRoles(string sessionGroup) =>
    [
        $"webpubsub.joinLeaveGroup.{sessionGroup}",
        $"webpubsub.sendToGroup.{sessionGroup}"
    ];

    private static TimeSpan CalculateRefreshLeadTime(TimeSpan tokenLifetime)
    {
        var leadTime = TimeSpan.FromSeconds(Math.Max(30, tokenLifetime.TotalSeconds * 0.2));
        return leadTime >= tokenLifetime
            ? TimeSpan.FromSeconds(tokenLifetime.TotalSeconds / 2)
            : TimeSpan.FromSeconds(Math.Min(TimeSpan.FromMinutes(10).TotalSeconds, leadTime.TotalSeconds));
    }
}
