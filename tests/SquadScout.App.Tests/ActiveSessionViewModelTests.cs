using SquadScout.App.Configuration;
using SquadScout.App.Services;
using SquadScout.App.ViewModels;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.Tests;

public sealed class ActiveSessionViewModelTests
{
    [Fact]
    public async Task InitializeAsync_WithoutActiveSession_ShowsTranscriptEmptyState()
    {
        var viewModel = CreateViewModel(
            connectionService: new RecordingMessageConnectionService(new MessageConnectionStatus
            {
                State = MessageConnectionState.Disconnected,
                Summary = "Messaging disconnected."
            }));

        await viewModel.InitializeAsync();

        Assert.False(viewModel.HasActiveSession);
        Assert.Equal("No active session", viewModel.EmptyStateTitle);
        Assert.Equal("Return to projects and start a session to open the transcript timeline.", viewModel.EmptyStateDescription);
        Assert.Equal("Start a session to compose messages.", viewModel.ComposerPlaceholder);
        Assert.Equal("No project selected", viewModel.ProjectName);
        Assert.False(viewModel.CanSendMessage);
        Assert.False(viewModel.RefreshStatusCommand.CanExecute(null));
        Assert.False(viewModel.ClearLocalShellContextCommand.CanExecute(null));
    }

    [Fact]
    public async Task InitializeAsync_BrokerPendingSession_ShowsPreviewBannersAndComposerGating()
    {
        var activeState = new ActiveSessionState();
        activeState.SetActiveSession(
            CreateProject("squadscout", "SquadScout"),
            CreateSession("squadscout", "session-15", SessionState.Pending),
            SessionActivationSource.Broker,
            "Pending broker-backed session.");

        var messageConnection = new RecordingMessageConnectionService(new MessageConnectionStatus
        {
            State = MessageConnectionState.Ready,
            Summary = "Messaging composition is ready for the session.",
            Hub = "squadscout",
            SupportsLiveSessionStream = false,
            SessionId = "session-15"
        });

        var viewModel = CreateViewModel(activeSessionState: activeState, connectionService: messageConnection);
        await viewModel.InitializeAsync();

        Assert.True(viewModel.HasActiveSession);
        Assert.True(viewModel.CanRefreshFromBroker);
        Assert.True(viewModel.RefreshStatusCommand.CanExecute(null));
        Assert.Equal("Transcript ready", viewModel.EmptyStateTitle);
        Assert.Equal("Draft a message in the native transcript preview.", viewModel.ComposerPlaceholder);
        Assert.Contains(viewModel.StatusBanners, banner => banner.Title == "Session pending");
        Assert.Contains(viewModel.StatusBanners, banner => banner.Title == "Transcript preview");
        Assert.False(viewModel.CanSendMessage);

        viewModel.ComposerText = "Hello from mobile";

        Assert.True(viewModel.CanSendMessage);
        Assert.True(viewModel.SendMessageCommand.CanExecute(null));

        await viewModel.SendMessageCommand.ExecuteAsync();

        Assert.Single(viewModel.TranscriptMessages);
        Assert.Contains("native transcript preview", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshStatusAsync_WhenBrokerSessionIsMissing_ClearsShellContextAndReturnsToProjects()
    {
        var activeState = new ActiveSessionState();
        activeState.SetActiveSession(
            CreateProject("squadscout", "SquadScout"),
            CreateSession("squadscout", "session-15", SessionState.Running),
            SessionActivationSource.Broker,
            "Broker-backed session.");

        var lifecycle = new RecordingSessionLifecycleService
        {
            OnGetAsync = _ => Task.FromResult<SessionDescriptor?>(null)
        };

        var messageConnection = new RecordingMessageConnectionService(new MessageConnectionStatus
        {
            State = MessageConnectionState.Connected,
            Summary = "Connected to session stream.",
            SupportsLiveSessionStream = true,
            SessionId = "session-15"
        });
        messageConnection.OnResetAsync = () => Task.FromResult(new MessageConnectionStatus
        {
            State = MessageConnectionState.Disconnected,
            Summary = "Messaging disconnected."
        });

        var navigator = new RecordingNavigator();
        var viewModel = CreateViewModel(
            activeSessionState: activeState,
            connectionService: messageConnection,
            navigator: navigator,
            sessionLifecycle: lifecycle);

        await viewModel.InitializeAsync();
        await viewModel.RefreshStatusCommand.ExecuteAsync();

        Assert.False(activeState.GetSnapshot().HasActiveSession);
        Assert.Equal(1, lifecycle.GetCallCount);
        Assert.Equal(1, messageConnection.ResetCallCount);
        Assert.Equal(1, navigator.GoToProjectsCallCount);
        Assert.Equal("Messaging disconnected.", viewModel.MessagingSummary);
        Assert.Contains("Cleared the mobile shell context only.", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.ClearLocalShellContextCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReconnectLiveTransportAsync_WhenReconnectFaults_ReportsFailureReason()
    {
        var activeState = new ActiveSessionState();
        activeState.SetActiveSession(
            CreateProject("squadscout", "SquadScout"),
            CreateSession("squadscout", "session-15", SessionState.Running),
            SessionActivationSource.Broker,
            "Broker-backed session.");

        var messageConnection = new RecordingMessageConnectionService(new MessageConnectionStatus
        {
            State = MessageConnectionState.Reconnecting,
            Summary = "Reconnecting to session stream...",
            SupportsLiveSessionStream = true,
            SessionId = "session-15"
        });
        messageConnection.OnReconnectAsync = () => Task.FromResult(new MessageConnectionStatus
        {
            State = MessageConnectionState.Faulted,
            Summary = "Reconnect failed.",
            FailureReason = "Socket unavailable.",
            SupportsLiveSessionStream = true,
            SessionId = "session-15"
        });

        var viewModel = CreateViewModel(activeSessionState: activeState, connectionService: messageConnection);
        await viewModel.InitializeAsync();
        await viewModel.ReconnectLiveTransportCommand.ExecuteAsync();

        Assert.Equal(1, messageConnection.ReconnectCallCount);
        Assert.Equal("Reconnect failed.", viewModel.StatusMessage);
        Assert.Equal("Reconnect failed.", viewModel.MessagingSummary);
        Assert.Equal("Socket unavailable.", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task InitializeAsync_DevelopmentFallbackSession_DisablesBrokerRefreshAndShowsOfflineBanner()
    {
        var activeState = new ActiveSessionState();
        activeState.SetActiveSession(
            CreateProject("squadscout", "SquadScout"),
            CreateSession("squadscout", "localdev-session-15", SessionState.Running),
            SessionActivationSource.DevelopmentFallback,
            "Offline development session.");

        var viewModel = CreateViewModel(
            activeSessionState: activeState,
            connectionService: new RecordingMessageConnectionService(new MessageConnectionStatus
            {
                State = MessageConnectionState.Ready,
                Summary = "Preview mode",
                SupportsLiveSessionStream = false,
                SessionId = "localdev-session-15"
            }));

        await viewModel.InitializeAsync();

        Assert.False(viewModel.CanRefreshFromBroker);
        Assert.False(viewModel.RefreshStatusCommand.CanExecute(null));
        Assert.Equal("Local development fallback", viewModel.SourceSummary);
        Assert.Contains(viewModel.StatusBanners, banner => banner.Title == "Offline development mode");
        Assert.Equal("Draft a message in the native transcript preview.", viewModel.ComposerPlaceholder);
    }

    private static ActiveSessionViewModel CreateViewModel(
        ActiveSessionState? activeSessionState = null,
        StubAuthenticationService? authenticationService = null,
        RecordingMessageConnectionService? connectionService = null,
        RecordingNavigator? navigator = null,
        RecordingSessionLifecycleService? sessionLifecycle = null)
    {
        return new ActiveSessionViewModel(
            new AppEnvironment(AppEnvironment.DevelopmentName),
            new BrokerApiOptions { BaseUrl = "http://127.0.0.1:5071" },
            activeSessionState ?? new ActiveSessionState(),
            sessionLifecycle ?? new RecordingSessionLifecycleService(),
            authenticationService ?? new StubAuthenticationService(new ClientIdentity("ryan", "Ryan Graham", "BrokerNegotiated")),
            connectionService ?? new RecordingMessageConnectionService(),
            navigator ?? new RecordingNavigator());
    }

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
