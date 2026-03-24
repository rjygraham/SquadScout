using System.Collections.Concurrent;
using SquadScout.Contracts.Projects;

namespace SquadScout.Broker.Projects;

public sealed class InMemoryProjectCatalog : IProjectCatalog
{
    private readonly ConcurrentDictionary<string, RegisteredProject> _projects = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyCollection<RegisteredProject>> ListAsync(CancellationToken cancellationToken = default)
    {
        var projects = _projects.Values
            .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyCollection<RegisteredProject>>(projects);
    }

    public Task UpsertAsync(RegisteredProject project, CancellationToken cancellationToken = default)
    {
        _projects[project.ProjectId] = project;
        return Task.CompletedTask;
    }
}
