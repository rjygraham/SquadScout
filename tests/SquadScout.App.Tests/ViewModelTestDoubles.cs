using System.Diagnostics;
using SquadScout.App.Navigation;
using SquadScout.App.Services;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.Navigation
{
    public interface IAppNavigator
    {
        Task GoToProjectsAsync();

        Task GoToActiveSessionAsync();
    }
}

namespace SquadScout.App.ViewModels
{
    internal static class MainThread
    {
        public static void BeginInvokeOnMainThread(Action action)
        {
            action();
        }
    }
}

namespace SquadScout.App.Tests
{
    internal sealed class RecordingNavigator : IAppNavigator
    {
        public int GoToActiveSessionCallCount { get; private set; }

        public int GoToProjectsCallCount { get; private set; }

        public Task GoToProjectsAsync()
        {
            GoToProjectsCallCount++;
            return Task.CompletedTask;
        }

        public Task GoToActiveSessionAsync()
        {
            GoToActiveSessionCallCount++;
            return Task.CompletedTask;
        }
    }

    internal sealed class StubAuthenticationService(ClientIdentity identity) : IAuthenticationService
    {
        public int CallCount { get; private set; }

        public Task<ClientIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(identity);
        }
    }

    internal sealed class ScriptedProjectCatalogService : IProjectCatalogService
    {
        private readonly Queue<Func<Task<ProjectCatalogSnapshot>>> _responses = new();

        public int CallCount { get; private set; }

        public void EnqueueFailure(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            _responses.Enqueue(() => Task.FromException<ProjectCatalogSnapshot>(exception));
        }

        public void EnqueueResult(ProjectCatalogSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            _responses.Enqueue(() => Task.FromResult(snapshot));
        }

        public void EnqueueTask(Task<ProjectCatalogSnapshot> task)
        {
            ArgumentNullException.ThrowIfNull(task);
            _responses.Enqueue(() => task);
        }

        public Task<ProjectCatalogSnapshot> GetProjectsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No scripted catalog response was configured.");
            }

            return _responses.Dequeue().Invoke();
        }
    }

    internal sealed class RecordingSessionLifecycleService : ISessionLifecycleService
    {
        public Func<string, Task<SessionDescriptor?>>? OnGetAsync { get; set; }

        public Func<StartSessionCommand, Task<SessionLaunchResult>>? OnStartAsync { get; set; }

        public int GetCallCount { get; private set; }

        public StartSessionCommand? LastStartCommand { get; private set; }

        public int StartCallCount { get; private set; }

        public Task<SessionDescriptor?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCallCount++;
            return OnGetAsync is null
                ? Task.FromResult<SessionDescriptor?>(null)
                : OnGetAsync(sessionId);
        }

        public Task<SessionLaunchResult> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCallCount++;
            LastStartCommand = command;

            if (OnStartAsync is null)
            {
                throw new InvalidOperationException("No scripted start response was configured.");
            }

            return OnStartAsync(command);
        }
    }

    internal sealed class RecordingMessageConnectionService : IMessageConnectionService
    {
        private MessageConnectionStatus _currentStatus;
        private readonly List<MessageEnvelopeTraffic> _recentTraffic = [];

        public RecordingMessageConnectionService(MessageConnectionStatus? currentStatus = null)
        {
            _currentStatus = currentStatus ?? new MessageConnectionStatus
            {
                State = MessageConnectionState.Disconnected,
                Summary = "Messaging disconnected."
            };
        }

        public event EventHandler<MessageConnectionStatus>? StatusChanged;

        public event EventHandler<MessageEnvelopeTraffic>? TrafficObserved;

        public MessageConnectionStatus CurrentStatus => _currentStatus;

        public IReadOnlyList<MessageEnvelopeTraffic> RecentTraffic => _recentTraffic;

        public Func<Task<MessageConnectionStatus>>? OnReconnectAsync { get; set; }

        public Func<Task<MessageConnectionStatus>>? OnResetAsync { get; set; }

        public Func<SessionDescriptor, MessageConnectionResumeState?, Task<MessageConnectionStatus>>? OnPrepareForSessionAsync { get; set; }

        public int PrepareForSessionCallCount { get; private set; }

        public int ReconnectCallCount { get; private set; }

        public int ResetCallCount { get; private set; }

        public Task SendInputAsync(string content, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendEnvelopeAsync<TPayload>(MessageEnvelope<TPayload> envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async Task<MessageConnectionStatus> PrepareForSessionAsync(
            SessionDescriptor session,
            MessageConnectionResumeState? resumeState = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareForSessionCallCount++;
            var status = OnPrepareForSessionAsync is null
                ? _currentStatus
                : await OnPrepareForSessionAsync(session, resumeState);

            SetCurrentStatus(status);
            return status;
        }

        public async Task<MessageConnectionStatus> ReconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReconnectCallCount++;
            var status = OnReconnectAsync is null
                ? _currentStatus
                : await OnReconnectAsync();

            SetCurrentStatus(status);
            return status;
        }

        public async Task<MessageConnectionStatus> ResetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResetCallCount++;
            var status = OnResetAsync is null
                ? new MessageConnectionStatus
                {
                    State = MessageConnectionState.Disconnected,
                    Summary = "Messaging disconnected."
                }
                : await OnResetAsync();

            SetCurrentStatus(status);
            return status;
        }

        public void PublishStatus(MessageConnectionStatus status)
        {
            SetCurrentStatus(status);
        }

        public void PublishTraffic(MessageEnvelopeTraffic traffic)
        {
            _recentTraffic.Add(traffic);
            TrafficObserved?.Invoke(this, traffic);
        }

        private void SetCurrentStatus(MessageConnectionStatus status)
        {
            _currentStatus = status;
            StatusChanged?.Invoke(this, status);
        }
    }

    internal sealed class RecordingSessionResumeService : ISessionResumeService
    {
        public Func<Task>? OnRestoreAsync { get; set; }

        public ActiveSessionResumeState? CurrentState { get; private set; }

        public int ClearCallCount { get; private set; }

        public int RestoreCallCount { get; private set; }

        public int SaveCallCount { get; private set; }

        public Task RestoreAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreCallCount++;
            return OnRestoreAsync?.Invoke() ?? Task.CompletedTask;
        }

        public Task SaveAsync(ActiveSessionResumeState state, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCallCount++;
            CurrentState = state;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearCallCount++;
            CurrentState = null;
            return Task.CompletedTask;
        }

        public void SetCurrentState(ActiveSessionResumeState? state)
        {
            CurrentState = state;
        }
    }

    internal static class AsyncAssert
    {
        public static async Task WaitForAsync(Func<bool> condition, string failureMessage, int timeoutMilliseconds = 1000)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(15);
            }

            throw new TimeoutException(failureMessage);
        }
    }
}
