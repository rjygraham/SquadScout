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
    private readonly SessionTranscriptController _transcriptController;

    private string _authenticationSummary = string.Empty;
    private bool _canRefreshFromBroker;
    private bool _canComposeMessage;
    private string _composerPlaceholder = "Start a session to compose messages.";
    private string _composerText = string.Empty;
    private string _emptyStateDescription = "Start a session to open the transcript timeline.";
    private string _emptyStateTitle = "No active session";
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
    private IReadOnlyList<TranscriptBannerState> _statusBanners = Array.Empty<TranscriptBannerState>();
    private IReadOnlyList<TranscriptMessageState> _transcriptMessages = Array.Empty<TranscriptMessageState>();

    private ClientIdentity? _identity;

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
        _activeSessionState = activeSessionState;
        _sessionLifecycleService = sessionLifecycleService;
        _authenticationService = authenticationService;
        _messageConnectionService = messageConnectionService;
        _navigator = navigator;
        _transcriptController = new SessionTranscriptController();

        RefreshStatusCommand = new AsyncCommand(RefreshStatusAsync, () => HasActiveSession && CanRefreshFromBroker && !IsBusy);
        ReturnToProjectsCommand = new AsyncCommand(ReturnToProjectsAsync, () => !IsBusy);
        ClearLocalShellContextCommand = new AsyncCommand(ClearLocalShellContextAsync, () => HasActiveSession && !IsBusy);
        SendMessageCommand = new AsyncCommand(SendMessageAsync, () => CanSendMessage && !IsBusy);
        ReconnectLiveTransportCommand = new AsyncCommand(ReconnectLiveTransportAsync, () => HasActiveSession && !IsBusy);

        _activeSessionState.Changed += (_, snapshot) => ApplySnapshot(snapshot);
        _messageConnectionService.StatusChanged += (_, status) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                MessagingSummary = status.Summary;
                ApplyTranscriptState(_transcriptController.Sync(_activeSessionState.GetSnapshot(), status));
            });

        StatusMessage = "Use the composer below to preview the native transcript workflow.";
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

    public bool CanSendMessage
    {
        get => _canComposeMessage && !string.IsNullOrWhiteSpace(ComposerText);
    }

    public IAsyncCommand ClearLocalShellContextCommand { get; }

    public string ComposerPlaceholder
    {
        get => _composerPlaceholder;
        private set => SetProperty(ref _composerPlaceholder, value);
    }

    public string ComposerText
    {
        get => _composerText;
        set
        {
            if (SetProperty(ref _composerText, value))
            {
                OnPropertyChanged(nameof(CanSendMessage));
                RefreshCommands();
            }
        }
    }

    public string EmptyStateDescription
    {
        get => _emptyStateDescription;
        private set => SetProperty(ref _emptyStateDescription, value);
    }

    public string EmptyStateTitle
    {
        get => _emptyStateTitle;
        private set => SetProperty(ref _emptyStateTitle, value);
    }

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

    public bool HasStatusBanners => StatusBanners.Count > 0;

    public bool HasTranscriptMessages => TranscriptMessages.Count > 0;

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

    public IAsyncCommand ReconnectLiveTransportCommand { get; }

    public IAsyncCommand RefreshStatusCommand { get; }

    public IAsyncCommand ReturnToProjectsCommand { get; }

    public IAsyncCommand SendMessageCommand { get; }

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

    public IReadOnlyList<TranscriptBannerState> StatusBanners
    {
        get => _statusBanners;
        private set
        {
            if (SetProperty(ref _statusBanners, value))
            {
                OnPropertyChanged(nameof(HasStatusBanners));
            }
        }
    }

    public string StartedAt
    {
        get => _startedAt;
        private set => SetProperty(ref _startedAt, value);
    }

    public IReadOnlyList<TranscriptMessageState> TranscriptMessages
    {
        get => _transcriptMessages;
        private set
        {
            if (SetProperty(ref _transcriptMessages, value))
            {
                OnPropertyChanged(nameof(HasTranscriptMessages));
            }
        }
    }

    public async Task InitializeAsync()
    {
        if (!_initialized)
        {
            _identity = await _authenticationService.GetCurrentIdentityAsync();
            AuthenticationSummary = $"{_identity.DisplayName} • {_identity.Mode}";
            _initialized = true;
        }

        var snapshot = _activeSessionState.GetSnapshot();
        ApplySnapshot(snapshot);
        MessagingSummary = _messageConnectionService.CurrentStatus.Summary;
        ApplyTranscriptState(_transcriptController.Sync(snapshot, _messageConnectionService.CurrentStatus));
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
            ApplyTranscriptState(_transcriptController.Sync(snapshot, _messageConnectionService.CurrentStatus));
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
        ApplyTranscriptState(_transcriptController.Sync(snapshot, _messageConnectionService.CurrentStatus));
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
        SendMessageCommand.RaiseCanExecuteChanged();
        ReconnectLiveTransportCommand.RaiseCanExecuteChanged();
    }

    private async Task ReconnectLiveTransportAsync()
    {
        if (!HasActiveSession)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        RefreshCommands();

        try
        {
            var status = await _messageConnectionService.ReconnectAsync();
            MessagingSummary = status.Summary;
            StatusMessage = status.Summary;
            ErrorMessage = status.State == MessageConnectionState.Faulted
                ? status.FailureReason ?? status.Summary
                : string.Empty;
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

    private void ApplyTranscriptState(SessionTranscriptViewState viewState)
    {
        StatusBanners = viewState.Banners;
        TranscriptMessages = viewState.Messages;
        ComposerPlaceholder = viewState.ComposerPlaceholder;
        EmptyStateTitle = viewState.EmptyTitle;
        EmptyStateDescription = viewState.EmptyDescription;
        _canComposeMessage = viewState.CanCompose;
        OnPropertyChanged(nameof(CanSendMessage));
        RefreshCommands();
    }

    private async Task SendMessageAsync()
    {
        _identity ??= await _authenticationService.GetCurrentIdentityAsync();

        var result = _transcriptController.SendDraft(
            _activeSessionState.GetSnapshot(),
            _messageConnectionService.CurrentStatus,
            _identity.DisplayName,
            ComposerText);

        ApplyTranscriptState(result.ViewState);
        if (!result.Success)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        ErrorMessage = string.Empty;
        ComposerText = string.Empty;
        StatusMessage = result.StatusMessage;
    }

    private Task ReturnToProjectsAsync() => _navigator.GoToProjectsAsync();
}
