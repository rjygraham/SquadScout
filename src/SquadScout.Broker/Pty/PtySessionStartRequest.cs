namespace SquadScout.Broker.Pty;

public sealed record PtySessionStartRequest
{
    public string SessionId { get; init; } = string.Empty;

    public string ProjectId { get; init; } = string.Empty;

    public string WorkingDirectory { get; init; } = string.Empty;

    public string[] Arguments { get; init; } = Array.Empty<string>();
}
