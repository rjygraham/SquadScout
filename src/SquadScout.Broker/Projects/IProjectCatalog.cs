using SquadScout.Contracts.Projects;

namespace SquadScout.Broker.Projects;

public interface IProjectCatalog
{
    Task<IReadOnlyCollection<RegisteredProject>> ListAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(RegisteredProject project, CancellationToken cancellationToken = default);
}
