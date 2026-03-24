namespace SquadScout.Contracts.Projects;

public sealed record RegisteredProject
{
    public string ProjectId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string RepositoryRoot { get; init; } = string.Empty;
}
