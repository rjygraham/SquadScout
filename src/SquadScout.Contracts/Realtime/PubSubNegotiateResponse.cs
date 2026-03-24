namespace SquadScout.Contracts.Realtime;

public sealed record PubSubNegotiateResponse
{
    public string Url { get; init; } = string.Empty;

    public string Hub { get; init; } = string.Empty;

    public string UserId { get; init; } = string.Empty;

    public string ProjectId { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public PubSubParticipantKind ParticipantKind { get; init; } = PubSubParticipantKind.Client;

    public string? BrokerId { get; init; }

    public string SessionGroup { get; init; } = string.Empty;

    public string PrincipalId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string IdentityProvider { get; init; } = string.Empty;

    public bool IsDevelopmentIdentity { get; init; }

    public string[] Roles { get; init; } = [];

    public string[] AutoJoinGroups { get; init; } = [];

    public DateTimeOffset ExpiresAtUtc { get; init; }

    public DateTimeOffset RefreshAtUtc { get; init; }
}
