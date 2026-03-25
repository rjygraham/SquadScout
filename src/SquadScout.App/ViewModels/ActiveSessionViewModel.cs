using SquadScout.App.Configuration;
using SquadScout.App.Navigation;
using SquadScout.App.Services;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.ViewModels;

public sealed class ActiveSessionViewModel : ViewModelBase
{
    private readonly IActiveSessionState _activeSessionState;
    private readonly IAuthenticationService _authenticationService;
    private readonly IMessageConnectionService _messageConnectionService;
    private readonly IAppNavigator _navigator;
    private readonly ISessionLifecycleService _sessionLifecycleService;

    private string _authenticationSummary = string.Empty;
    private bool _canRefreshFromBroker;
    private bool _hasActiveSession;
    private bool _initialized;
    private string _messagingSummary = string.Empty;
    private string _projectName = "No project selected";
    private string _projectRoot = "—";
    private string _sessionId = "—";
    private string _sessionState = "—";
    private string _sessionSummary = "No active session selected.";
    private string _sourceSummary = "No session source";
    private string _startedAt = "—";

    public ActiveSessionViewModel(
        AppEnvironment environment,
        BrokerApiOptions brokerApiOptions,
        IActiveSessionState activeSessionState,
        ISessionLifecycleService sessionLifecycleService,
        IAuthenticationService authenticationService,
        IMessageConnectionService messageConnectionService,
        IAppNavigator navigator)
    {
        EnvironmentSummary = $"{environment.Name} • {brokerApiOptions.BaseUrl}";
        TranscriptPlaceholder = "Transcript, reconnect, and voice interactions land in #10, #21, #27, and #28.";

        _activeSessionState = activeSessionState;
        _sessionLifecycleService = sessionLifecycleService;
        _authenticationService = authenticationService;
        _messageConnectionService = messageConnectionService;
        _navigator = navigator;

        RefreshStatusCommand = new AsyncCommand(RefreshStatusAsync, () => HasActiveSession && CanRefreshFromBroker && !IsBusy);
        ReturnToProjectsCommand = new AsyncCommand(ReturnToProjectsAsync, () => !IsBusy);
        ClearLocalShellContextCommand = new AsyncCommand(ClearLocalShellContextAsync, () => HasActiveSession && !IsBusy);

        _activeSessionState.Changed += (_, snapshot) => ApplySnapshot(snapshot);

        StatusMessage = "Session control will render here once a pending session is active.";
        ApplySnapshot(_activeSessionState.GetSnapshot());
        MessagingSummary = _messageConnectionService.CurrentStatus.Summary;
    }

    public string AuthenticationSummary
    {
        get => _authenticationSummary;
        private set => SetProperty(ref _authenticationSummary, value);
    }

    public bool CanRefreshFromBroker
    {
        get => _canRefreshFromBroker;
        private set
        {
            if (SetProperty(ref _canRefreshFromBroker, value))
            {
                RefreshCommands();
            }
        }
    }

    public IAsyncCommand ClearLocalShellContextCommand { get; }

    public string EnvironmentSummary { get; }

    public bool HasActiveSession
    {
        get => _hasActiveSession;
        private set
        {
            if (SetProperty(ref _hasActiveSession, value))
            {
                RefreshCommands();
            }
        }
    }

    public string MessagingSummary
    {
        get => _messagingSummary;
        private set => SetProperty(ref _messagingSummary, value);
    }

    public string ProjectName
    {
        get => _projectName;
        private set => SetProperty(ref _projectName, value);
    }

    public string ProjectRoot
    {
        get => _projectRoot;
        private set => SetProperty(ref _projectRoot, value);
    }

    public IAsyncCommand RefreshStatusCommand { get; }

    public IAsyncCommand ReturnToProjectsCommand { get; }

    public string SessionId
    {
        get => _sessionId;
        private set => SetProperty(ref _sessionId, value);
    }

    public string SessionState
    {
        get => _sessionState;
        private set => SetProperty(ref _sessionState, value);
    }

    public string SessionSummary
    {
        get => _sessionSummary;
        private set => SetProperty(ref _sessionSummary, value);
    }

    public string SourceSummary
    {
        get => _sourceSummary;
        private set => SetProperty(ref _sourceSummary, value);
    }

    public string StartedAt
    {
        get => _startedAt;
        private set => SetProperty(ref _startedAt, value);
    }

    public string TranscriptPlaceholder { get; }

    public async Task InitializeAsync()
    {
        if (!_initialized)
        {
            var identity = await _authenticationService.GetCurrentIdentityAsync();
            AuthenticationSummary = $"{identity.DisplayName} • {identity.Mode}";
            _initialized = true;
        }

        ApplySnapshot(_activeSessionState.GetSnapshot());
        MessagingSummary = _messageConnectionService.CurrentStatus.Summary;
    }

    private void ApplySnapshot(ActiveSessionSnapshot snapshot)
    {
        HasActiveSession = snapshot.HasActiveSession;
        SessionSummary = snapshot.Summary;

        if (!snapshot.HasActiveSession || snapshot.Project is null || snapshot.Session is null)
        {
            ProjectName = "No project selected";
            ProjectRoot = "—";
            SessionId = "—";
            SessionState = "—";
            StartedAt = "—";
            SourceSummary = "No session source";
            CanRefreshFromBroker = false;
            return;
        }

        ProjectName = snapshot.Project.DisplayName;
        ProjectRoot = snapshot.Project.RepositoryRoot;
        SessionId = snapshot.Session.SessionId;
        SessionState = snapshot.Session.State.ToString();
        StartedAt = snapshot.Session.CreatedAtUtc.ToLocalTime().ToString("g");
        SourceSummary = snapshot.Source == SessionActivationSource.DevelopmentFallback
            ? "Local development fallback"
            : "Broker-backed shell";
        CanRefreshFromBroker = snapshot.Source == SessionActivationSource.Broker;
    }

    private async Task ClearLocalShellContextAsync()
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        RefreshCommands();

        try
        {
            var summary = _activeSessionState.GetSnapshot().Source == SessionActivationSource.Broker
                ? "Cleared the mobile shell context only. Remote stop control lands with later lifecycle work."
                : "Cleared the local-development session scaffold.";

            await _messageConnectionService.ResetAsync();
            MessagingSummary = _messageConnectionService.CurrentStatus.Summary;
            _activeSessionState.Clear(summary);
            StatusMessage = summary;
            await _navigator.GoToProjectsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    private void RefreshCommands()
    {
        RefreshStatusCommand.RaiseCanExecuteChanged();
        ReturnToProjectsCommand.RaiseCanExecuteChanged();
        ClearLocalShellContextCommand.RaiseCanExecuteChanged();
    }

    private async Task RefreshStatusAsync()
    {
        var snapshot = _activeSessionState.GetSnapshot();
        if (!snapshot.HasActiveSession || snapshot.Session is null)
        {
            return;
        }

        if (!CanRefreshFromBroker)
        {
            StatusMessage = "Local-development session shells are not broker-backed, so there is nothing to poll yet.";
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        RefreshCommands();

        try
        {
            var refreshedSession = await _sessionLifecycleService.GetAsync(snapshot.Session.SessionId);
            if (refreshedSession is null)
            {
                await ClearLocalShellContextAsync();
                return;
            }

            _activeSessionState.UpdateSession(refreshedSession, "Refreshed session state from the broker.");
            StatusMessage = "Refreshed session state from the broker.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    private Task ReturnToProjectsAsync() => _navigator.GoToProjectsAsync();
}
