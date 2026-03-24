using System.Collections.Concurrent;
using SquadScout.Contracts.Projects;

namespace SquadScout.Broker.Projects;

public sealed class InMemoryProjectCatalog : IProjectCatalog
{
    private readonly ConcurrentDictionary<string, RegisteredProject> _projects = new(StringComparer.OrdinalIgnoreCase);

    public Task<RegisteredProject?> GetAsync(string projectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("A project id is required.", nameof(projectId));
        }

        _projects.TryGetValue(projectId, out var project);
        return Task.FromResult(project);
    }

    public Task<IReadOnlyCollection<RegisteredProject>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var projects = _projects.Values
            .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyCollection<RegisteredProject>>(projects);
    }

    public Task UpsertAsync(RegisteredProject project, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(project);

        _projects[project.ProjectId] = project;
        return Task.CompletedTask;
    }
}
