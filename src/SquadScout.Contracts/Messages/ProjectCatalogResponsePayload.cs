using SquadScout.Contracts.Projects;

namespace SquadScout.Contracts.Messages;

public sealed record ProjectCatalogResponsePayload
{
    public IReadOnlyList<RegisteredProject> Projects { get; init; } = Array.Empty<RegisteredProject>();

    public string Summary { get; init; } = string.Empty;

    public string? Error { get; init; }
}
