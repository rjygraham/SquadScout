using Orleans;

namespace SquadScout.Broker.Orleans;

public interface ISessionGrainFactory
{
    ISessionGrain GetGrain(string sessionId);
}

public sealed class OrleansSessionGrainFactory : ISessionGrainFactory
{
    private readonly IGrainFactory _grainFactory;

    public OrleansSessionGrainFactory(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
    }

    public ISessionGrain GetGrain(string sessionId) => _grainFactory.GetGrain<ISessionGrain>(sessionId);
}
