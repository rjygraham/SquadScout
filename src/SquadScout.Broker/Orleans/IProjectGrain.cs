using Orleans;

namespace SquadScout.Broker.Orleans;

public interface IProjectGrain : IGrainWithStringKey
{
    Task<RegisteredProjectRecord?> GetAsync();

    Task<RegisteredProjectRecord> UpsertAsync(RegisteredProjectRecord project);
}

public interface IProjectGrainFactory
{
    IProjectGrain GetGrain(string projectId);
}

public sealed class OrleansProjectGrainFactory : IProjectGrainFactory
{
    private readonly IGrainFactory _grainFactory;

    public OrleansProjectGrainFactory(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
    }

    public IProjectGrain GetGrain(string projectId) => _grainFactory.GetGrain<IProjectGrain>(projectId);
}
