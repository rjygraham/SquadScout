namespace SquadScout.Contracts.Messages;

public sealed record HeartbeatPayload
{
    public bool ReplayRequested { get; init; }

    public int ExpectedIntervalSeconds { get; init; } = SessionHeartbeatDefaults.ExpectedIntervalSeconds;

    public int? LivenessTimeoutSeconds { get; init; }

    public string? SenderInstanceId { get; init; }

    public string? Nonce { get; init; }

    public string? AcknowledgedNonce { get; init; }
}
