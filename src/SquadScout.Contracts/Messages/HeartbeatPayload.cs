namespace SquadScout.Contracts.Messages;

public sealed record HeartbeatPayload
{
    public bool ReplayRequested { get; init; }

    public int ExpectedIntervalSeconds { get; init; } = 30;

    public string SenderInstanceId { get; init; } = string.Empty;
}
