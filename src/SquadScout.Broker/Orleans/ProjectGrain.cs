using Orleans.Runtime;

namespace SquadScout.Broker.Orleans;

public sealed class ProjectGrain : Grain, IProjectGrain
{
    private readonly IPersistentState<ProjectGrainState> _persistentState;
    private readonly object _loadGateSync = new();
    private LoadGateState _loadGate = new();
    private RegisteredProjectRecord? _project;
    private volatile bool _isDeactivating;
    private volatile bool _stateLoaded;

    public ProjectGrain(
        [PersistentState("project", Configuration.OrleansHostOptions.DefaultStorageProvider)]
        IPersistentState<ProjectGrainState> persistentState)
    {
        _persistentState = persistentState ?? throw new ArgumentNullException(nameof(persistentState));
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _isDeactivating = false;
        await EnsureStateLoadedAsync().ConfigureAwait(true);
        await base.OnActivateAsync(cancellationToken).ConfigureAwait(true);
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _isDeactivating = true;
        _project = null;
        _stateLoaded = false;
        RetireLoadGate();
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    public async Task<RegisteredProjectRecord?> GetAsync()
    {
        await EnsureStateLoadedAsync().ConfigureAwait(true);
        return _project?.Clone();
    }

    public async Task<RegisteredProjectRecord> UpsertAsync(RegisteredProjectRecord project)
    {
        ArgumentNullException.ThrowIfNull(project);
        await EnsureStateLoadedAsync().ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(project.ProjectId))
        {
            throw new ArgumentException("A project id is required.", nameof(project));
        }

        _project = project.Clone();
        await PersistAsync().ConfigureAwait(true);
        return _project.Clone();
    }

    private async Task PersistAsync()
    {
        if (_project is null)
        {
            _persistentState.State = new ProjectGrainState();
            await _persistentState.ClearStateAsync().ConfigureAwait(true);
            return;
        }

        _persistentState.State = new ProjectGrainState
        {
            Project = _project.Clone()
        };
        await _persistentState.WriteStateAsync().ConfigureAwait(true);
    }

    private async Task EnsureStateLoadedAsync()
    {
        if (_stateLoaded || _isDeactivating)
        {
            return;
        }

        var loadGate = RentLoadGate();
        await loadGate.Semaphore.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_stateLoaded || _isDeactivating || loadGate.IsRetired)
            {
                return;
            }

            await _persistentState.ReadStateAsync().ConfigureAwait(true);
            _project = _persistentState.RecordExists
                ? _persistentState.State.Project?.Clone()
                : null;
            _stateLoaded = true;
        }
        finally
        {
            loadGate.Semaphore.Release();
            ReleaseLoadGateLease(loadGate);
        }
    }

    private LoadGateState RentLoadGate()
    {
        lock (_loadGateSync)
        {
            _loadGate.LeaseCount++;
            return _loadGate;
        }
    }

    private void RetireLoadGate()
    {
        lock (_loadGateSync)
        {
            var retiredGate = _loadGate;
            retiredGate.IsRetired = true;
            _loadGate = new LoadGateState();
            TryDisposeRetiredLoadGate(retiredGate);
        }
    }

    private void ReleaseLoadGateLease(LoadGateState loadGate)
    {
        lock (_loadGateSync)
        {
            loadGate.LeaseCount--;
            TryDisposeRetiredLoadGate(loadGate);
        }
    }

    private static void TryDisposeRetiredLoadGate(LoadGateState loadGate)
    {
        if (!loadGate.IsRetired || loadGate.LeaseCount != 0)
        {
            return;
        }

        loadGate.Semaphore.Dispose();
    }

    private sealed class LoadGateState
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int LeaseCount { get; set; }

        public bool IsRetired { get; set; }
    }
}
