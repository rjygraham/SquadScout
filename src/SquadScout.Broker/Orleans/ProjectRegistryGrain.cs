using Orleans.Runtime;

namespace SquadScout.Broker.Orleans;

public sealed class ProjectRegistryGrain : Grain, IProjectRegistryGrain
{
    private readonly IPersistentState<ProjectRegistryGrainState> _persistentState;
    private readonly object _loadGateSync = new();
    private LoadGateState _loadGate = new();
    private ProjectRegistryGrainState _state = new();
    private volatile bool _isDeactivating;
    private volatile bool _stateLoaded;

    public ProjectRegistryGrain(
        [PersistentState("project-registry", Configuration.OrleansHostOptions.DefaultStorageProvider)]
        IPersistentState<ProjectRegistryGrainState> persistentState)
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
        _state = new ProjectRegistryGrainState();
        _stateLoaded = false;
        RetireLoadGate();
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    public async Task<ProjectRegistrySnapshotRecord> GetAsync()
    {
        await EnsureStateLoadedAsync().ConfigureAwait(true);
        return _state.ToSnapshot();
    }

    public async Task UpsertAsync(RegisteredProjectRecord project)
    {
        ArgumentNullException.ThrowIfNull(project);
        await EnsureStateLoadedAsync().ConfigureAwait(true);

        UpsertCore(project);
        await PersistAsync().ConfigureAwait(true);
    }

    public async Task ImportPhase1SeedAsync(List<RegisteredProjectRecord> projects, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(projects);
        await EnsureStateLoadedAsync().ConfigureAwait(true);

        foreach (var project in projects)
        {
            if (project is null)
            {
                continue;
            }

            UpsertCore(project);
        }

        _state.Phase1SeedImported = true;
        _state.Phase1SeedImportedAtUtc ??= importedAtUtc;
        await PersistAsync().ConfigureAwait(true);
    }

    private void UpsertCore(RegisteredProjectRecord project)
    {
        if (string.IsNullOrWhiteSpace(project.ProjectId))
        {
            throw new ArgumentException("A project id is required.", nameof(project));
        }

        var projects = _state.Projects;
        var existingIndex = projects.FindIndex(existing =>
            string.Equals(existing.ProjectId, project.ProjectId, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            projects[existingIndex] = project.Clone();
        }
        else
        {
            projects.Add(project.Clone());
        }

        projects.Sort(static (left, right) =>
        {
            var displayNameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
            return displayNameComparison != 0
                ? displayNameComparison
                : StringComparer.OrdinalIgnoreCase.Compare(left.ProjectId, right.ProjectId);
        });
    }

    private Task PersistAsync()
    {
        _persistentState.State = _state.ToSnapshot().ToState();
        return _persistentState.WriteStateAsync();
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
            _state = _persistentState.RecordExists
                ? _persistentState.State.ToSnapshot().ToState()
                : new ProjectRegistryGrainState();
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
