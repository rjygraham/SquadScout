using SquadScout.App.Configuration;
using SquadScout.App.Services;
using SquadScout.App.ViewModels;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.Tests;

public sealed class ProjectSelectionViewModelTests
{
    [Fact]
    public async Task InitializeAsync_TracksLoadingStateUntilProjectsArrive()
    {
        var projectCatalog = new ScriptedProjectCatalogService();
        var refreshGate = new TaskCompletionSource<ProjectCatalogSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        projectCatalog.EnqueueTask(refreshGate.Task);

        var viewModel = CreateViewModel(
            projectCatalog: projectCatalog,
            connectionService: new RecordingMessageConnectionService());

        var initializeTask = viewModel.InitializeAsync();

        await AsyncAssert.WaitForAsync(
            () => viewModel.IsBusy && viewModel.IsRefreshing,
            "Project selection never entered the loading state.");

        Assert.False(viewModel.RefreshCommand.CanExecute(null));
        Assert.False(viewModel.StartSessionCommand.CanExecute(null));
        Assert.False(viewModel.ResumeActiveSessionCommand.CanExecute(null));

        refreshGate.SetResult(CreateCatalogSnapshot(CreateProject("squadscout", "SquadScout")));
        await initializeTask;

        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsRefreshing);
        Assert.Single(viewModel.Projects);
        Assert.Equal("SquadScout", viewModel.SelectedProject?.DisplayName);
        Assert.True(viewModel.StartSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshAsync_WhenCatalogBecomesEmpty_ClearsSelectionAndDisablesStart()
    {
        var projectCatalog = new ScriptedProjectCatalogService();
        projectCatalog.EnqueueResult(CreateCatalogSnapshot(CreateProject("squadscout", "SquadScout")));
        projectCatalog.EnqueueResult(new ProjectCatalogSnapshot([], ProjectCatalogSource.Broker, "The broker is reachable, but no projects are registered yet."));

        var viewModel = CreateViewModel(projectCatalog: projectCatalog);
        await viewModel.InitializeAsync();

        Assert.Equal("SquadScout", viewModel.SelectedProject?.DisplayName);
        Assert.True(viewModel.StartSessionCommand.CanExecute(null));

        await viewModel.RefreshCommand.ExecuteAsync();

        Assert.Empty(viewModel.Projects);
        Assert.Null(viewModel.SelectedProject);
        Assert.False(viewModel.StartSessionCommand.CanExecute(null));
        Assert.Equal("The broker is reachable, but no projects are registered yet.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task RefreshAsync_AllowsRetryAfterFailure()
    {
        var projectCatalog = new ScriptedProjectCatalogService();
        projectCatalog.EnqueueFailure(new InvalidOperationException("Broker refresh failed."));
        projectCatalog.EnqueueResult(CreateCatalogSnapshot(CreateProject("squadscout", "SquadScout")));

        var viewModel = CreateViewModel(projectCatalog: projectCatalog);
        await viewModel.InitializeAsync();

        Assert.Equal("Broker refresh failed.", viewModel.ErrorMessage);
        Assert.True(viewModel.HasError);
        Assert.False(viewModel.StartSessionCommand.CanExecute(null));

        await viewModel.RefreshCommand.ExecuteAsync();

        Assert.False(viewModel.HasError);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
        Assert.Single(viewModel.Projects);
        Assert.Equal("SquadScout", viewModel.SelectedProject?.DisplayName);
        Assert.True(viewModel.StartSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task ActiveSessionSnapshot_DisablesStartAndUsesResumeFlow()
    {
        var project = CreateProject("squadscout", "SquadScout");
        var activeState = new ActiveSessionState();
        activeState.SetActiveSession(
            project,
            CreateSession(project.ProjectId, "session-15", SessionState.Pending),
            SessionActivationSource.Broker,
            "Resume the pending session.");

        var navigator = new RecordingNavigator();
        var projectCatalog = new ScriptedProjectCatalogService();
        projectCatalog.EnqueueResult(CreateCatalogSnapshot(project));

        var viewModel = CreateViewModel(
            activeSessionState: activeState,
            navigator: navigator,
            projectCatalog: projectCatalog);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.HasActiveSession);
        Assert.Equal("Resume the pending session.", viewModel.SessionSummary);
        Assert.False(viewModel.StartSessionCommand.CanExecute(null));
        Assert.True(viewModel.ResumeActiveSessionCommand.CanExecute(null));

        await viewModel.ResumeActiveSessionCommand.ExecuteAsync();

        Assert.Equal(1, navigator.GoToActiveSessionCallCount);
    }

    [Fact]
    public async Task InitializeAsync_RestoresSavedSessionBeforeShowingResumeCard()
    {
        var project = CreateProject("squadscout", "SquadScout");
        var activeState = new ActiveSessionState();
        var resumeService = new RecordingSessionResumeService
        {
            OnRestoreAsync = () =>
            {
                activeState.SetActiveSession(
                    project,
                    CreateSession(project.ProjectId, "session-restored", SessionState.Running),
                    SessionActivationSource.Broker,
                    "Recovered from this device.");
                return Task.CompletedTask;
            }
        };

        var projectCatalog = new ScriptedProjectCatalogService();
        projectCatalog.EnqueueResult(CreateCatalogSnapshot(project));

        var viewModel = CreateViewModel(
            activeSessionState: activeState,
            sessionResumeService: resumeService,
            projectCatalog: projectCatalog);

        await viewModel.InitializeAsync();

        Assert.Equal(1, resumeService.RestoreCallCount);
        Assert.True(viewModel.HasActiveSession);
        Assert.Equal("Recovered from this device.", viewModel.SessionSummary);
        Assert.True(viewModel.ResumeActiveSessionCommand.CanExecute(null));
        Assert.False(viewModel.StartSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartSessionAsync_NormalizesRequestAndNavigatesToTranscript()
    {
        var project = CreateProject("squadscout", "SquadScout");
        var projectCatalog = new ScriptedProjectCatalogService();
        projectCatalog.EnqueueResult(CreateCatalogSnapshot(project));

        var lifecycle = new RecordingSessionLifecycleService
        {
            OnStartAsync = command => Task.FromResult(new SessionLaunchResult(
                CreateSession(command.ProjectId, "session-15", SessionState.Pending),
                SessionActivationSource.Broker,
                "Started a broker-backed pending session."))
        };

        var messageConnection = new RecordingMessageConnectionService();
        messageConnection.OnPrepareForSessionAsync = (session, _) => Task.FromResult(new MessageConnectionStatus
        {
            State = MessageConnectionState.Ready,
            Summary = "Messaging composition is ready for the session.",
            SessionId = session.SessionId,
            SupportsLiveSessionStream = false
        });

        var activeState = new ActiveSessionState();
        var resumeService = new RecordingSessionResumeService();
        var navigator = new RecordingNavigator();
        var viewModel = CreateViewModel(
            activeSessionState: activeState,
            navigator: navigator,
            projectCatalog: projectCatalog,
            sessionLifecycle: lifecycle,
            connectionService: messageConnection,
            sessionResumeService: resumeService);

        await viewModel.InitializeAsync();
        viewModel.RequestedBy = "   ";
        viewModel.CommandArguments = "--continue --project \"src\\SquadScout.App\"";

        await viewModel.StartSessionCommand.ExecuteAsync();

        Assert.NotNull(lifecycle.LastStartCommand);
        Assert.Equal("mobile-user", lifecycle.LastStartCommand!.RequestedBy);
        Assert.Equal(project.ProjectId, lifecycle.LastStartCommand.ProjectId);
        Assert.Equal(["--continue", "--project", @"src\SquadScout.App"], lifecycle.LastStartCommand.Arguments);
        Assert.Equal(1, messageConnection.PrepareForSessionCallCount);
        Assert.Equal(1, navigator.GoToActiveSessionCallCount);

        var snapshot = activeState.GetSnapshot();
        Assert.True(snapshot.HasActiveSession);
        Assert.Equal("session-15", snapshot.Session?.SessionId);
        Assert.Contains("Started a broker-backed pending session.", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Messaging composition is ready for the session.", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, resumeService.SaveCallCount);
    }

    private static ProjectSelectionViewModel CreateViewModel(
        ActiveSessionState? activeSessionState = null,
        StubAuthenticationService? authenticationService = null,
        RecordingMessageConnectionService? connectionService = null,
        RecordingSessionResumeService? sessionResumeService = null,
        RecordingNavigator? navigator = null,
        ScriptedProjectCatalogService? projectCatalog = null,
        RecordingSessionLifecycleService? sessionLifecycle = null)
    {
        return new ProjectSelectionViewModel(
            new AppEnvironment(AppEnvironment.DevelopmentName),
            new BrokerApiOptions { BaseUrl = "http://127.0.0.1:5071" },
            projectCatalog ?? new ScriptedProjectCatalogService(),
            sessionLifecycle ?? new RecordingSessionLifecycleService(),
            authenticationService ?? new StubAuthenticationService(new ClientIdentity("ryan", "Ryan Graham", "BrokerNegotiated")),
            connectionService ?? new RecordingMessageConnectionService(),
            sessionResumeService ?? new RecordingSessionResumeService(),
            activeSessionState ?? new ActiveSessionState(),
            navigator ?? new RecordingNavigator());
    }

    private static ProjectCatalogSnapshot CreateCatalogSnapshot(params RegisteredProject[] projects) =>
        new(projects, ProjectCatalogSource.Broker, $"Loaded {projects.Length} project(s) from the broker.");

    private static RegisteredProject CreateProject(string projectId, string displayName) =>
        new()
        {
            ProjectId = projectId,
            DisplayName = displayName,
            RepositoryRoot = $@"D:\GitHub\{displayName}"
        };

    private static SessionDescriptor CreateSession(string projectId, string sessionId, SessionState state) =>
        new()
        {
            SessionId = sessionId,
            ProjectId = projectId,
            State = state,
            CreatedAtUtc = new DateTimeOffset(2026, 03, 25, 11, 30, 00, TimeSpan.Zero)
        };
}
