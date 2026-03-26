using Orleans;
using SquadScout.Contracts.Projects;

namespace SquadScout.Broker.Orleans;

[GenerateSerializer]
public sealed class RegisteredProjectRecord
{
    [Id(0)]
    public string ProjectId { get; set; } = string.Empty;

    [Id(1)]
    public string DisplayName { get; set; } = string.Empty;

    [Id(2)]
    public string RepositoryRoot { get; set; } = string.Empty;
}

[GenerateSerializer]
public sealed class ProjectGrainState
{
    [Id(0)]
    public RegisteredProjectRecord? Project { get; set; }
}

[GenerateSerializer]
public sealed class ProjectRegistrySnapshotRecord
{
    [Id(0)]
    public List<RegisteredProjectRecord> Projects { get; set; } = [];

    [Id(1)]
    public bool Phase1SeedImported { get; set; }

    [Id(2)]
    public DateTimeOffset? Phase1SeedImportedAtUtc { get; set; }
}

[GenerateSerializer]
public sealed class ProjectRegistryGrainState
{
    [Id(0)]
    public List<RegisteredProjectRecord> Projects { get; set; } = [];

    [Id(1)]
    public bool Phase1SeedImported { get; set; }

    [Id(2)]
    public DateTimeOffset? Phase1SeedImportedAtUtc { get; set; }
}

internal static class ProjectGrainSerialization
{
    public static RegisteredProjectRecord ToRecord(this RegisteredProject project) =>
        new()
        {
            ProjectId = project.ProjectId,
            DisplayName = project.DisplayName,
            RepositoryRoot = project.RepositoryRoot
        };

    public static RegisteredProject ToRegisteredProject(this RegisteredProjectRecord project) =>
        new()
        {
            ProjectId = project.ProjectId,
            DisplayName = project.DisplayName,
            RepositoryRoot = project.RepositoryRoot
        };

    public static RegisteredProjectRecord Clone(this RegisteredProjectRecord project) =>
        new()
        {
            ProjectId = project.ProjectId,
            DisplayName = project.DisplayName,
            RepositoryRoot = project.RepositoryRoot
        };

    public static ProjectRegistrySnapshotRecord ToSnapshot(this ProjectRegistryGrainState state) =>
        new()
        {
            Projects = state.Projects.Select(Clone).ToList(),
            Phase1SeedImported = state.Phase1SeedImported,
            Phase1SeedImportedAtUtc = state.Phase1SeedImportedAtUtc
        };

    public static ProjectRegistryGrainState ToState(this ProjectRegistrySnapshotRecord snapshot) =>
        new()
        {
            Projects = snapshot.Projects.Select(Clone).ToList(),
            Phase1SeedImported = snapshot.Phase1SeedImported,
            Phase1SeedImportedAtUtc = snapshot.Phase1SeedImportedAtUtc
        };
}
