namespace SquadScout.Contracts.Sessions;

public sealed record StopSessionCommand
{
    public string ProjectId { get; init; } = string.Empty;

    public string RequestedBy { get; init; } = string.Empty;

    public string Reason { get; init; } = "client-requested-stop";
}
