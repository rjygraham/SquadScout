namespace SquadScout.Contracts.Sessions;

public sealed record StartSessionCommand
{
    public string ProjectId { get; init; } = string.Empty;

    public string RequestedBy { get; init; } = string.Empty;

    public string[] Arguments { get; init; } = Array.Empty<string>();
}
