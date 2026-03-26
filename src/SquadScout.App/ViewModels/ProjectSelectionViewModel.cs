using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using SquadScout.App.Configuration;
using SquadScout.App.Navigation;
using SquadScout.App.Services;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.ViewModels;

public sealed class ProjectSelectionViewModel : ViewModelBase
{
    private readonly IActiveSessionState _activeSessionState;
    private readonly IAuthenticationService _authenticationService;
    private readonly IMessageConnectionService _messageConnectionService;
    private readonly IAppNavigator _navigator;
    private readonly IProjectCatalogService _projectCatalogService;
    private readonly ISessionResumeService _sessionResumeService;
    private readonly ISessionLifecycleService _sessionLifecycleService;

    private ActiveSessionSnapshot _activeSessionSnapshot = ActiveSessionSnapshot.Empty;
    private string _authenticationSummary = string.Empty;
    private string _commandArguments = string.Empty;
    private bool _hasActiveSession;
    private bool _initialized;
    private bool _isRefreshing;
    private ProjectCatalogSource _projectCatalogSource = ProjectCatalogSource.Broker;
    private string _requestedBy = string.Empty;
    private RegisteredProject? _selectedProject;
    private string _sessionSummary = "No active session selected.";

    public ProjectSelectionViewModel(
        AppEnvironment environment,
        BrokerApiOptions brokerApiOptions,
        IProjectCatalogService projectCatalogService,
        ISessionLifecycleService sessionLifecycleService,
        IAuthenticationService authenticationService,
        IMessageConnectionService messageConnectionService,
        ISessionResumeService sessionResumeService,
        IActiveSessionState activeSessionState,
        IAppNavigator navigator)
    {
        EnvironmentSummary = $"{environment.Name} • {brokerApiOptions.BaseUrl}";
        LowConcurrencySummary = "This mobile shell keeps one active session in focus at a time.";

        _projectCatalogService = projectCatalogService;
        _sessionLifecycleService = sessionLifecycleService;
        _authenticationService = authenticationService;
        _messageConnectionService = messageConnectionService;
        _sessionResumeService = sessionResumeService;
        _activeSessionState = activeSessionState;
        _navigator = navigator;

        Projects = [];
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        StartSessionCommand = new AsyncCommand(StartSessionAsync, CanStartSession);
        ResumeActiveSessionCommand = new AsyncCommand(ResumeActiveSessionAsync, () => HasActiveSession && !IsBusy);

        _activeSessionState.Changed += (_, snapshot) => ApplyActiveSession(snapshot);

        StatusMessage = "Load a project and prepare a pending session from here.";
        ApplyActiveSession(_activeSessionState.GetSnapshot());
    }

    public string AuthenticationSummary
    {
        get => _authenticationSummary;
        private set => SetProperty(ref _authenticationSummary, value);
    }

    public string CommandArguments
    {
        get => _commandArguments;
        set => SetProperty(ref _commandArguments, value);
    }

    public string EnvironmentSummary { get; }

    public bool HasActiveSession
    {
        get => _hasActiveSession;
        private set
        {
            if (SetProperty(ref _hasActiveSession, value))
            {
                NotifyProjectPresentationChanged();
                RefreshCommands();
            }
        }
    }

    public bool HasProjects => Projects.Count > 0;

    public bool HasSelectedProject => SelectedProject is not null;

    public bool HasSelectionWarning => !string.IsNullOrWhiteSpace(SelectionWarningMessage);

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    public string LowConcurrencySummary { get; }

    public ObservableCollection<RegisteredProject> Projects { get; }

    public string ProjectsStateDescription =>
        IsRefreshing && !HasProjects
            ? "Checking the broker and any local development seeds for available repositories."
            : HasError
                ? ErrorMessage
                : _projectCatalogSource == ProjectCatalogSource.DevelopmentFallback
                    ? "No local seed projects are available right now. Start the broker or add development seeds, then retry."
                    : "Register a repo in the broker and retry when you're ready.";

    public string ProjectsStateTitle =>
        IsRefreshing && !HasProjects
            ? "Loading projects"
            : HasError
                ? "Couldn't refresh projects"
                : _projectCatalogSource == ProjectCatalogSource.DevelopmentFallback
                    ? "No development projects"
                    : "No projects registered";

    public IAsyncCommand RefreshCommand { get; }

    public string RequestedBy
    {
        get => _requestedBy;
        set => SetProperty(ref _requestedBy, value);
    }

    public bool ShowSessionStartCard => !HasActiveSession && HasSelectedProject;

    public string SelectedProjectName => SelectedProject?.DisplayName ?? "No project selected";

    public string SelectedProjectPath => string.IsNullOrWhiteSpace(SelectedProject?.RepositoryRoot)
        ? "Pick a registered repository to continue."
        : SelectedProject.RepositoryRoot;

    public string SelectedProjectSummary
    {
        get
        {
            if (SelectedProject is null)
            {
                return "Choose a project to unlock the next session.";
            }

            if (HasActiveSession &&
                _activeSessionSnapshot.Project is not null &&
                IsSameProject(_activeSessionSnapshot.Project, SelectedProject))
            {
                return "This project already owns the in-focus mobile session.";
            }

            return _projectCatalogSource == ProjectCatalogSource.DevelopmentFallback
                ? "Development fallback project for offline/mobile UX checks."
                : "Broker-registered project ready for session start.";
        }
    }

    public IAsyncCommand ResumeActiveSessionCommand { get; }

    public RegisteredProject? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                NotifyProjectPresentationChanged();
                RefreshCommands();
            }
        }
    }

    public string SelectionWarningMessage
    {
        get
        {
            if (HasActiveSession &&
                _activeSessionSnapshot.Project is not null &&
                SelectedProject is not null &&
                !IsSameProject(_activeSessionSnapshot.Project, SelectedProject))
            {
                return $"Only one session stays in focus at a time. Resume '{_activeSessionSnapshot.Project.DisplayName}' or clear it from the transcript page before switching to '{SelectedProject.DisplayName}'.";
            }

            if (HasActiveSession &&
                _activeSessionSnapshot.Project is not null &&
                !HasCatalogEntry(_activeSessionSnapshot.Project))
            {
                return $"The active session project '{_activeSessionSnapshot.Project.DisplayName}' is not in the latest broker list. You can still resume it, or clear it before switching.";
            }

            if (!HasActiveSession && SelectedProject is not null && !HasCatalogEntry(SelectedProject))
            {
                return $"'{SelectedProject.DisplayName}' is no longer in the current project list. Refresh or choose another project before starting.";
            }

            return string.Empty;
        }
    }

    public string SelectionWarningTitle =>
        HasSelectionWarning && HasActiveSession
            ? "Session switch paused"
            : HasSelectionWarning
                ? "Selection needs attention"
                : string.Empty;

    public string SessionSummary
    {
        get => _sessionSummary;
        private set => SetProperty(ref _sessionSummary, value);
    }

    public string SessionActionDescription
    {
        get
        {
            if (HasActiveSession)
            {
                return "Keep the current session going from the transcript, or clear it there before starting another project.";
            }

            return SelectedProject is null
                ? "Pick a project from the list above to unlock session start."
                : $"Start a pending session for '{SelectedProject.DisplayName}' and move straight into the chat-style transcript.";
        }
    }

    public string SessionActionTitle => HasActiveSession ? "Session already in focus" : "Start a session";

    public IAsyncCommand StartSessionCommand { get; }

    public async Task InitializeAsync()
    {
        await _sessionResumeService.RestoreAsync();

        if (_initialized)
        {
            ApplyActiveSession(_activeSessionState.GetSnapshot());
            return;
        }

        _initialized = true;
        await LoadIdentityAsync();
        await RefreshAsync();
    }

    private void ApplyActiveSession(ActiveSessionSnapshot snapshot)
    {
        _activeSessionSnapshot = snapshot;
        HasActiveSession = snapshot.HasActiveSession;
        SessionSummary = snapshot.Summary;

        if (snapshot.Project is not null)
        {
            if (SelectedProject is null ||
                !HasProjects ||
                IsSameProject(SelectedProject, snapshot.Project))
            {
                SelectedProject = TryGetCatalogProject(snapshot.Project.ProjectId) ?? snapshot.Project;
                return;
            }
        }

        if (!snapshot.HasActiveSession && SelectedProject is not null && !HasCatalogEntry(SelectedProject))
        {
            SelectedProject = Projects.FirstOrDefault();
            return;
        }

        NotifyProjectPresentationChanged();
    }

    private bool CanStartSession() =>
        !IsBusy && !HasActiveSession && SelectedProject is not null && HasCatalogEntry(SelectedProject);

    private async Task LoadIdentityAsync()
    {
        var identity = await _authenticationService.GetCurrentIdentityAsync();
        RequestedBy = identity.RequestedBy;
        AuthenticationSummary = $"{identity.DisplayName} • {identity.Mode}";
    }

    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        IsRefreshing = true;
        ErrorMessage = string.Empty;
        NotifyProjectPresentationChanged();
        RefreshCommands();

        try
        {
            var snapshot = await _projectCatalogService.GetProjectsAsync();
            _projectCatalogSource = snapshot.Source;

            Projects.Clear();
            foreach (var project in snapshot.Projects)
            {
                Projects.Add(project);
            }

            if (HasActiveSession)
            {
                var activeProject = _activeSessionSnapshot.Project;
                if (activeProject is not null)
                {
                    SelectedProject = TryGetCatalogProject(activeProject.ProjectId) ?? activeProject;
                }
                else
                {
                    SelectedProject = Projects.FirstOrDefault();
                }
            }
            else if (SelectedProject is null || !HasCatalogEntry(SelectedProject))
            {
                SelectedProject = Projects.FirstOrDefault();
            }

            StatusMessage = snapshot.Summary;
            NotifyProjectPresentationChanged();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = HasProjects
                ? "Showing the last loaded project list. Retry when the broker is reachable again."
                : "Project loading failed. Retry once the broker or development seeds are available.";
            NotifyProjectPresentationChanged();
        }
        finally
        {
            IsRefreshing = false;
            IsBusy = false;
            NotifyProjectPresentationChanged();
            RefreshCommands();
        }
    }

    private void RefreshCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        StartSessionCommand.RaiseCanExecuteChanged();
        ResumeActiveSessionCommand.RaiseCanExecuteChanged();
    }

    private async Task ResumeActiveSessionAsync()
    {
        if (!HasActiveSession)
        {
            return;
        }

        StatusMessage = $"Resuming the in-focus session for '{_activeSessionSnapshot.Project?.DisplayName ?? "the selected project"}'.";
        NotifyProjectPresentationChanged();
        await _navigator.GoToActiveSessionAsync();
    }

    private async Task StartSessionAsync()
    {
        if (SelectedProject is null)
        {
            ErrorMessage = "Select a project before starting a session.";
            return;
        }

        if (HasActiveSession)
        {
            ErrorMessage = string.Empty;
            StatusMessage = HasSelectionWarning
                ? SelectionWarningMessage
                : $"A session is already in focus for '{SelectedProject.DisplayName}'. Resume it to continue.";
            NotifyProjectPresentationChanged();
            return;
        }

        if (!HasCatalogEntry(SelectedProject))
        {
            ErrorMessage = $"'{SelectedProject.DisplayName}' is no longer in the current project list. Refresh and choose a valid project before starting.";
            NotifyProjectPresentationChanged();
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        NotifyProjectPresentationChanged();
        RefreshCommands();

        try
        {
            var requestedBy = string.IsNullOrWhiteSpace(RequestedBy)
                ? "mobile-user"
                : RequestedBy.Trim();

            var launchResult = await _sessionLifecycleService.StartAsync(new StartSessionCommand
            {
                ProjectId = SelectedProject.ProjectId,
                RequestedBy = requestedBy,
                Arguments = ParseArguments(CommandArguments)
            });

            _activeSessionState.SetActiveSession(SelectedProject, launchResult.Session, launchResult.Source, launchResult.Summary);

            var connectionStatus = await _messageConnectionService.PrepareForSessionAsync(launchResult.Session);
            await _sessionResumeService.SaveAsync(new ActiveSessionResumeState
            {
                Snapshot = _activeSessionState.GetSnapshot(),
                Connection = new MessageConnectionResumeState
                {
                    Generation = connectionStatus.Generation,
                    AcknowledgedSequence = connectionStatus.AcknowledgedSequence
                },
                RecentTraffic = _messageConnectionService.RecentTraffic.ToArray()
            });
            StatusMessage = $"{launchResult.Summary} {connectionStatus.Summary}";
            NotifyProjectPresentationChanged();

            await _navigator.GoToActiveSessionAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            NotifyProjectPresentationChanged();
        }
        finally
        {
            IsBusy = false;
            NotifyProjectPresentationChanged();
            RefreshCommands();
        }
    }

    private bool HasCatalogEntry(RegisteredProject project) =>
        Projects.Any(candidate => IsSameProject(candidate, project));

    private static bool IsSameProject(RegisteredProject left, RegisteredProject right) =>
        string.Equals(left.ProjectId, right.ProjectId, StringComparison.OrdinalIgnoreCase);

    private void NotifyProjectPresentationChanged()
    {
        OnPropertyChanged(nameof(CanRetryProjects));
        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(HasSelectedProject));
        OnPropertyChanged(nameof(HasSelectionWarning));
        OnPropertyChanged(nameof(ProjectsStateDescription));
        OnPropertyChanged(nameof(ProjectsStateTitle));
        OnPropertyChanged(nameof(SelectedProjectName));
        OnPropertyChanged(nameof(SelectedProjectPath));
        OnPropertyChanged(nameof(SelectedProjectSummary));
        OnPropertyChanged(nameof(SelectionWarningMessage));
        OnPropertyChanged(nameof(SelectionWarningTitle));
        OnPropertyChanged(nameof(SessionActionDescription));
        OnPropertyChanged(nameof(SessionActionTitle));
        OnPropertyChanged(nameof(ShowSessionStartCard));
    }

    public bool CanRetryProjects => !IsBusy;

    private static string[] ParseArguments(string commandArguments)
    {
        if (string.IsNullOrWhiteSpace(commandArguments))
        {
            return [];
        }

        return Regex.Matches(commandArguments, "\"([^\"]*)\"|(\\S+)")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .Where(argument => !string.IsNullOrWhiteSpace(argument))
            .ToArray();
    }

    private RegisteredProject? TryGetCatalogProject(string projectId) =>
        Projects.FirstOrDefault(project => string.Equals(project.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
}
