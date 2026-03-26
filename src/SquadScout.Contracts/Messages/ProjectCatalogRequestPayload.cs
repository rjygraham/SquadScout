namespace SquadScout.Contracts.Messages;

public sealed record ProjectCatalogRequestPayload
{
    public string RequestedBy { get; init; } = string.Empty;
}
