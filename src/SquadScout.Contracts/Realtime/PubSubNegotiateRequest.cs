namespace SquadScout.Contracts.Realtime;

public sealed record PubSubNegotiateRequest
{
    public string ProjectId { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public PubSubParticipantKind ParticipantKind { get; init; } = PubSubParticipantKind.Client;

    /// <summary>
    /// Optional broker-affinity suffix for later multi-broker routing. Leave unset in the single-broker
    /// Phase 1 path so participants share the base session group.
    /// </summary>
    public string? BrokerId { get; init; }
}
