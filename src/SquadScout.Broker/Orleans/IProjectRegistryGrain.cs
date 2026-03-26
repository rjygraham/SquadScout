using Orleans;

namespace SquadScout.Broker.Orleans;

public interface IProjectRegistryGrain : IGrainWithStringKey
{
    Task<ProjectRegistrySnapshotRecord> GetAsync();

    Task UpsertAsync(RegisteredProjectRecord project);

    Task ImportPhase1SeedAsync(List<RegisteredProjectRecord> projects, DateTimeOffset importedAtUtc);
}

public interface IProjectRegistryGrainFactory
{
    IProjectRegistryGrain GetGrain();
}

public sealed class OrleansProjectRegistryGrainFactory : IProjectRegistryGrainFactory
{
    private const string RegistryGrainKey = "projects";
    private readonly IGrainFactory _grainFactory;

    public OrleansProjectRegistryGrainFactory(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
    }

    public IProjectRegistryGrain GetGrain() => _grainFactory.GetGrain<IProjectRegistryGrain>(RegistryGrainKey);
}
