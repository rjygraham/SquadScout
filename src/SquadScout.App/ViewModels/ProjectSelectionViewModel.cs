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
    private readonly ISessionLifecycleService _sessionLifecycleService;

    private string _authenticationSummary = string.Empty;
    private string _commandArguments = string.Empty;
    private bool _hasActiveSession;
    private bool _initialized;
    private bool _isRefreshing;
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
        IActiveSessionState activeSessionState,
        IAppNavigator navigator)
    {
        EnvironmentSummary = $"{environment.Name} • {brokerApiOptions.BaseUrl}";
        LowConcurrencySummary = "This mobile shell keeps one active session in focus at a time.";

        _projectCatalogService = projectCatalogService;
        _sessionLifecycleService = sessionLifecycleService;
        _authenticationService = authenticationService;
        _messageConnectionService = messageConnectionService;
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
                RefreshCommands();
            }
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    public string LowConcurrencySummary { get; }

    public ObservableCollection<RegisteredProject> Projects { get; }

    public IAsyncCommand RefreshCommand { get; }

    public string RequestedBy
    {
        get => _requestedBy;
        set => SetProperty(ref _requestedBy, value);
    }

    public IAsyncCommand ResumeActiveSessionCommand { get; }

    public RegisteredProject? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                RefreshCommands();
            }
        }
    }

    public string SessionSummary
    {
        get => _sessionSummary;
        private set => SetProperty(ref _sessionSummary, value);
    }

    public IAsyncCommand StartSessionCommand { get; }

    public async Task InitializeAsync()
    {
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
        HasActiveSession = snapshot.HasActiveSession;
        SessionSummary = snapshot.Summary;

        if (snapshot.Project is not null)
        {
            SelectedProject ??= snapshot.Project;
        }
    }

    private bool CanStartSession() =>
        !IsBusy && !HasActiveSession && SelectedProject is not null;

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
        RefreshCommands();

        try
        {
            var snapshot = await _projectCatalogService.GetProjectsAsync();

            Projects.Clear();
            foreach (var project in snapshot.Projects)
            {
                Projects.Add(project);
            }

            if (HasActiveSession)
            {
                var activeProject = _activeSessionState.GetSnapshot().Project;
                SelectedProject = activeProject is null
                    ? SelectedProject
                    : Projects.FirstOrDefault(project => string.Equals(project.ProjectId, activeProject.ProjectId, StringComparison.OrdinalIgnoreCase)) ?? activeProject;
            }
            else if (SelectedProject is null && Projects.Count > 0)
            {
                SelectedProject = Projects[0];
            }

            StatusMessage = snapshot.Summary;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsRefreshing = false;
            IsBusy = false;
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
            await ResumeActiveSessionAsync();
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
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
            StatusMessage = $"{launchResult.Summary} {connectionStatus.Summary}";

            await _navigator.GoToActiveSessionAsync();
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
}
