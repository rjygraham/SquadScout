using System.Text.Json;
using SquadScout.App.Configuration;
using SquadScout.App.Navigation;
using SquadScout.App.Services;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.ViewModels;

public sealed class ActiveSessionViewModel : ViewModelBase
{
    private readonly IActiveSessionState _activeSessionState;
    private readonly IAuthenticationService _authenticationService;
    private readonly IMessageConnectionService _messageConnectionService;
    private readonly IAppNavigator _navigator;
    private readonly ISessionResumeService _sessionResumeService;
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
    private bool _suspendPersistence;
    private IReadOnlyList<TranscriptMessageState> _transcriptMessages = Array.Empty<TranscriptMessageState>();
    private string? _restoredTrafficSessionKey;

    private ClientIdentity? _identity;

    public ActiveSessionViewModel(
        AppEnvironment environment,
        BrokerApiOptions brokerApiOptions,
        IActiveSessionState activeSessionState,
        ISessionLifecycleService sessionLifecycleService,
        IAuthenticationService authenticationService,
        IMessageConnectionService messageConnectionService,
        ISessionResumeService sessionResumeService,
        IAppNavigator navigator)
    {
        EnvironmentSummary = $"{environment.Name} • {brokerApiOptions.BaseUrl}";
        _activeSessionState = activeSessionState;
        _sessionLifecycleService = sessionLifecycleService;
        _authenticationService = authenticationService;
        _messageConnectionService = messageConnectionService;
        _sessionResumeService = sessionResumeService;
        _navigator = navigator;
        _transcriptController = new SessionTranscriptController();

        RefreshStatusCommand = new AsyncCommand(RefreshStatusAsync, () => HasActiveSession && CanRefreshFromBroker && !IsBusy);
        ReturnToProjectsCommand = new AsyncCommand(ReturnToProjectsAsync, () => !IsBusy);
        ClearLocalShellContextCommand = new AsyncCommand(ClearLocalShellContextAsync, () => HasActiveSession && !IsBusy);
        SendMessageCommand = new AsyncCommand(SendMessageAsync, () => CanSendMessage && !IsBusy);
        ReconnectLiveTransportCommand = new AsyncCommand(ReconnectLiveTransportAsync, () => HasActiveSession && !IsBusy);

        _activeSessionState.Changed += (_, snapshot) =>
        {
            ApplySnapshot(snapshot);
            QueuePersistCurrentSession();
        };
        _messageConnectionService.StatusChanged += (_, status) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                MessagingSummary = status.Summary;
                ApplyTranscriptState(_transcriptController.Sync(_activeSessionState.GetSnapshot(), status));
                QueuePersistCurrentSession();
            });
        _messageConnectionService.TrafficObserved += (_, traffic) =>
            MainThread.BeginInvokeOnMainThread(() => HandleTrafficObserved(traffic));

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
                NotifySessionChromeChanged();
                RefreshCommands();
            }
        }
    }

    public bool HasStatusBanners => StatusBanners.Count > 0;

    public bool HasTranscriptMessages => TranscriptMessages.Count > 0;

    public string MessagingSummary
    {
        get => _messagingSummary;
        private set
        {
            if (SetProperty(ref _messagingSummary, value))
            {
                NotifySessionChromeChanged();
            }
        }
    }

    public string NavigationHint => HasActiveSession
        ? "Projects keeps this session available to resume. Clear removes the local shell context on this device."
        : "Return to projects to choose a repo and start a session.";

    public bool ShowProjectPickerAction => !HasActiveSession;

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
        private set
        {
            if (SetProperty(ref _sourceSummary, value))
            {
                NotifySessionChromeChanged();
            }
        }
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
        private set
        {
            if (SetProperty(ref _startedAt, value))
            {
                NotifySessionChromeChanged();
            }
        }
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
        _suspendPersistence = true;
        try
        {
            await _sessionResumeService.RestoreAsync();

            if (!_initialized)
            {
                _identity = await _authenticationService.GetCurrentIdentityAsync();
                AuthenticationSummary = $"{_identity.DisplayName} • {_identity.Mode}";
                _initialized = true;
            }

            var snapshot = _activeSessionState.GetSnapshot();
            ApplySnapshot(snapshot);
            RestorePersistedTrafficIfNeeded(snapshot);
            await EnsureRestoredSessionPreparedAsync(snapshot);
            MessagingSummary = _messageConnectionService.CurrentStatus.Summary;
            ApplyTranscriptState(_transcriptController.Sync(snapshot, _messageConnectionService.CurrentStatus));
        }
        finally
        {
            _suspendPersistence = false;
        }

        QueuePersistCurrentSession();
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
            _restoredTrafficSessionKey = null;
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
            await _sessionResumeService.ClearAsync();
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
            var snapshot = _activeSessionState.GetSnapshot();
            var status = await ReconnectCurrentSessionAsync(snapshot);
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

    private async Task<MessageConnectionStatus> ReconnectCurrentSessionAsync(ActiveSessionSnapshot snapshot)
    {
        if (!snapshot.HasActiveSession || snapshot.Session is null)
        {
            return _messageConnectionService.CurrentStatus;
        }

        var restoredState = _sessionResumeService.CurrentState;
        var canResumeFromStore = IsRestoredStateForSnapshot(restoredState, snapshot);
        var currentStatus = _messageConnectionService.CurrentStatus;
        if (string.Equals(currentStatus.SessionId, snapshot.Session.SessionId, StringComparison.OrdinalIgnoreCase) &&
            currentStatus.State is not MessageConnectionState.Disconnected)
        {
            return await _messageConnectionService.ReconnectAsync();
        }

        return await _messageConnectionService.PrepareForSessionAsync(
            snapshot.Session,
            canResumeFromStore ? restoredState!.Connection : null);
    }

    private void HandleTrafficObserved(MessageEnvelopeTraffic traffic)
    {
        var snapshot = _activeSessionState.GetSnapshot();
        if (!snapshot.HasActiveSession || snapshot.Session is null)
        {
            return;
        }

        ApplyTranscriptState(_transcriptController.ObserveTraffic(snapshot, _messageConnectionService.CurrentStatus, traffic));

        if (traffic.Direction == MessageTrafficDirection.Incoming &&
            traffic.Envelope.MessageType == SessionMessageType.SessionLifecycle)
        {
            var payload = traffic.Envelope.Payload.Deserialize<SessionLifecyclePayload>(SessionMessageSerializer.DefaultOptions);
            if (payload is not null && payload.State != snapshot.Session.State)
            {
                var summary = string.IsNullOrWhiteSpace(payload.Reason)
                    ? $"Session state updated to {payload.State} from the live transcript."
                    : payload.Reason;
                _activeSessionState.UpdateSession(snapshot.Session with { State = payload.State }, summary);
            }
        }

        QueuePersistCurrentSession();
    }

    private void RestorePersistedTrafficIfNeeded(ActiveSessionSnapshot snapshot)
    {
        if (!snapshot.HasActiveSession || snapshot.Session is null)
        {
            _restoredTrafficSessionKey = null;
            return;
        }

        var restoredState = _sessionResumeService.CurrentState;
        if (!IsRestoredStateForSnapshot(restoredState, snapshot))
        {
            return;
        }

        var sessionKey = $"{snapshot.Project!.ProjectId}:{snapshot.Session.SessionId}";
        if (string.Equals(_restoredTrafficSessionKey, sessionKey, StringComparison.Ordinal))
        {
            return;
        }

        ApplyTranscriptState(
            _transcriptController.RestoreFromTraffic(
                snapshot,
                _messageConnectionService.CurrentStatus,
                restoredState!.RecentTraffic));
        _restoredTrafficSessionKey = sessionKey;
    }

    private async Task EnsureRestoredSessionPreparedAsync(ActiveSessionSnapshot snapshot)
    {
        if (!snapshot.HasActiveSession || snapshot.Session is null || snapshot.Source != SessionActivationSource.Broker)
        {
            return;
        }

        var restoredState = _sessionResumeService.CurrentState;
        if (!IsRestoredStateForSnapshot(restoredState, snapshot))
        {
            return;
        }

        var currentStatus = _messageConnectionService.CurrentStatus;
        if (string.Equals(currentStatus.SessionId, snapshot.Session.SessionId, StringComparison.OrdinalIgnoreCase) &&
            currentStatus.State is MessageConnectionState.Connected or MessageConnectionState.Connecting or MessageConnectionState.Reconnecting)
        {
            return;
        }

        try
        {
            var refreshedSession = await _sessionLifecycleService.GetAsync(snapshot.Session.SessionId);
            if (refreshedSession is null)
            {
                await HandleMissingRestoredSessionAsync();
                return;
            }

            if (refreshedSession.State != snapshot.Session.State)
            {
                _activeSessionState.UpdateSession(refreshedSession, snapshot.Summary);
            }

            var status = await _messageConnectionService.PrepareForSessionAsync(refreshedSession, restoredState!.Connection);
            MessagingSummary = status.Summary;
            StatusMessage = status.Summary;
            ErrorMessage = status.State == MessageConnectionState.Faulted
                ? status.FailureReason ?? status.Summary
                : string.Empty;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Saved session restored locally. Retry reconnect when the broker is reachable again.";
        }
    }

    private async Task HandleMissingRestoredSessionAsync()
    {
        await _messageConnectionService.ResetAsync();
        await _sessionResumeService.ClearAsync();
        _activeSessionState.Clear("The saved session is no longer available. Start a new session to continue.");
        StatusMessage = "The saved session is no longer available.";
        await _navigator.GoToProjectsAsync();
    }

    private void QueuePersistCurrentSession()
    {
        if (_suspendPersistence)
        {
            return;
        }

        _ = PersistCurrentSessionAsync();
    }

    private async Task PersistCurrentSessionAsync()
    {
        try
        {
            var snapshot = _activeSessionState.GetSnapshot();
            if (!snapshot.HasActiveSession)
            {
                await _sessionResumeService.ClearAsync();
                return;
            }

            await _sessionResumeService.SaveAsync(new ActiveSessionResumeState
            {
                Snapshot = snapshot,
                Connection = new MessageConnectionResumeState
                {
                    Generation = _messageConnectionService.CurrentStatus.Generation,
                    AcknowledgedSequence = _messageConnectionService.CurrentStatus.AcknowledgedSequence
                },
                RecentTraffic = BuildPersistedTraffic(snapshot)
            });
        }
        catch
        {
        }
    }

    private static bool IsRestoredStateForSnapshot(ActiveSessionResumeState? restoredState, ActiveSessionSnapshot snapshot) =>
        restoredState?.Snapshot.Project is not null &&
        restoredState.Snapshot.Session is not null &&
        snapshot.Project is not null &&
        snapshot.Session is not null &&
        string.Equals(restoredState.Snapshot.Project.ProjectId, snapshot.Project.ProjectId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(restoredState.Snapshot.Session.SessionId, snapshot.Session.SessionId, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<MessageEnvelopeTraffic> BuildPersistedTraffic(ActiveSessionSnapshot snapshot)
    {
        var restoredState = _sessionResumeService.CurrentState;
        var baseline = IsRestoredStateForSnapshot(restoredState, snapshot)
            ? restoredState!.RecentTraffic
            : Array.Empty<MessageEnvelopeTraffic>();

        return baseline
            .Concat(_messageConnectionService.RecentTraffic)
            .GroupBy(CreateTrafficPersistenceKey, StringComparer.Ordinal)
            .Select(group => group.Last())
            .TakeLast(Math.Max(1, baseline.Count + _messageConnectionService.RecentTraffic.Count))
            .ToArray();
    }

    private static string CreateTrafficPersistenceKey(MessageEnvelopeTraffic traffic)
    {
        if (!string.IsNullOrWhiteSpace(traffic.Envelope.MessageId))
        {
            return $"{traffic.Direction}:{traffic.Envelope.MessageType}:{traffic.Envelope.MessageId}";
        }

        if (traffic.Envelope.Direction == MessageDirection.BrokerToClient &&
            traffic.Envelope.Sequence is long sequence)
        {
            return $"{traffic.Direction}:{traffic.Envelope.MessageType}:{traffic.Envelope.Generation}:{sequence}";
        }

        if (traffic.Envelope.Direction == MessageDirection.ClientToBroker &&
            traffic.Envelope.ClientSequence is long clientSequence)
        {
            return $"{traffic.Direction}:{traffic.Envelope.MessageType}:{clientSequence}";
        }

        return $"{traffic.Direction}:{traffic.Envelope.MessageType}:{traffic.ObservedAtUtc:O}";
    }

    private void NotifySessionChromeChanged()
    {
        OnPropertyChanged(nameof(NavigationHint));
        OnPropertyChanged(nameof(ShowProjectPickerAction));
    }

    private async Task SendMessageAsync()
    {
        _identity ??= await _authenticationService.GetCurrentIdentityAsync();

        var snapshot = _activeSessionState.GetSnapshot();
        var connectionStatus = _messageConnectionService.CurrentStatus;
        var usesLiveTransport = snapshot.Source == SessionActivationSource.Broker && connectionStatus.SupportsLiveSessionStream;

        var result = _transcriptController.SendDraft(
            snapshot,
            connectionStatus,
            _identity.DisplayName,
            ComposerText,
            appendDraftMessage: !usesLiveTransport);

        ApplyTranscriptState(result.ViewState);
        if (!result.Success)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        if (usesLiveTransport)
        {
            try
            {
                await _messageConnectionService.SendInputAsync(ComposerText.Trim());
                ErrorMessage = string.Empty;
                ComposerText = string.Empty;
                StatusMessage = result.StatusMessage;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }

            return;
        }

        ErrorMessage = string.Empty;
        ComposerText = string.Empty;
        StatusMessage = result.StatusMessage;
    }

    private Task ReturnToProjectsAsync() => _navigator.GoToProjectsAsync();
}
