using SquadScout.Broker.Orleans;
using SquadScout.Contracts.Projects;

namespace SquadScout.Broker.Projects;

public sealed class GrainBackedProjectCatalog : IProjectCatalog
{
    private readonly IProjectGrainFactory _projectGrainFactory;
    private readonly IProjectRegistryGrainFactory _projectRegistryGrainFactory;
    private readonly InMemoryProjectCatalog _phase1Catalog;
    private readonly SemaphoreSlim _phase1SeedGate = new(1, 1);
    private volatile bool _phase1SeedImported;

    public GrainBackedProjectCatalog(
        IProjectGrainFactory projectGrainFactory,
        IProjectRegistryGrainFactory projectRegistryGrainFactory,
        InMemoryProjectCatalog phase1Catalog)
    {
        _projectGrainFactory = projectGrainFactory ?? throw new ArgumentNullException(nameof(projectGrainFactory));
        _projectRegistryGrainFactory = projectRegistryGrainFactory ?? throw new ArgumentNullException(nameof(projectRegistryGrainFactory));
        _phase1Catalog = phase1Catalog ?? throw new ArgumentNullException(nameof(phase1Catalog));
    }

    public async Task<RegisteredProject?> GetAsync(string projectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("A project id is required.", nameof(projectId));
        }

        await EnsurePhase1SeedImportedAsync(cancellationToken).ConfigureAwait(false);

        var storedProject = await _projectGrainFactory.GetGrain(projectId).GetAsync().ConfigureAwait(false);
        if (storedProject is not null)
        {
            return storedProject.ToRegisteredProject();
        }

        var legacyProject = await _phase1Catalog.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (legacyProject is null)
        {
            return null;
        }

        return (await UpsertDurableAsync(legacyProject.ToRecord()).ConfigureAwait(false)).ToRegisteredProject();
    }

    public async Task<IReadOnlyCollection<RegisteredProject>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsurePhase1SeedImportedAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = await _projectRegistryGrainFactory.GetGrain().GetAsync().ConfigureAwait(false);
        return snapshot.Projects
            .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project.ProjectId, StringComparer.OrdinalIgnoreCase)
            .Select(project => project.ToRegisteredProject())
            .ToArray();
    }

    public async Task UpsertAsync(RegisteredProject project, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(project);

        await EnsurePhase1SeedImportedAsync(cancellationToken).ConfigureAwait(false);
        await _phase1Catalog.UpsertAsync(project, cancellationToken).ConfigureAwait(false);
        await UpsertDurableAsync(project.ToRecord()).ConfigureAwait(false);
    }

    private async Task EnsurePhase1SeedImportedAsync(CancellationToken cancellationToken)
    {
        if (_phase1SeedImported)
        {
            return;
        }

        await _phase1SeedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_phase1SeedImported)
            {
                return;
            }

            var phase1Projects = (await _phase1Catalog.ListAsync(cancellationToken).ConfigureAwait(false)).ToArray();
            if (phase1Projects.Length > 0)
            {
                var importedAtUtc = DateTimeOffset.UtcNow;
                var projectRecords = phase1Projects.Select(project => project.ToRecord()).ToList();
                foreach (var projectRecord in projectRecords)
                {
                    await _projectGrainFactory.GetGrain(projectRecord.ProjectId)
                        .UpsertAsync(projectRecord)
                        .ConfigureAwait(false);
                }

                await _projectRegistryGrainFactory.GetGrain()
                    .ImportPhase1SeedAsync(projectRecords, importedAtUtc)
                    .ConfigureAwait(false);
            }

            _phase1SeedImported = true;
        }
        finally
        {
            _phase1SeedGate.Release();
        }
    }

    private async Task<RegisteredProjectRecord> UpsertDurableAsync(RegisteredProjectRecord project)
    {
        var storedProject = await _projectGrainFactory.GetGrain(project.ProjectId)
            .UpsertAsync(project)
            .ConfigureAwait(false);
        await _projectRegistryGrainFactory.GetGrain()
            .UpsertAsync(storedProject)
            .ConfigureAwait(false);
        return storedProject;
    }
}
