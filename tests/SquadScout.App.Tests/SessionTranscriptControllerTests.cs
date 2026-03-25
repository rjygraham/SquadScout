using SquadScout.App.Services;
using SquadScout.App.ViewModels;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Sessions;

namespace SquadScout.App.Tests;

public sealed class SessionTranscriptControllerTests
{
    [Fact]
    public void Sync_BuildsPreviewBannersAndEmptyStateForPendingSession()
    {
        var controller = new SessionTranscriptController();

        var state = controller.Sync(
            CreateSnapshot(SessionState.Pending, SessionActivationSource.Broker),
            new MessageConnectionStatus(
                MessageConnectionState.Ready,
                "Messaging composition is ready for the session.",
                "squadscout",
                SupportsLiveSessionStream: false));

        Assert.True(state.CanCompose);
        Assert.Equal("Transcript ready", state.EmptyTitle);
        Assert.Contains(state.Banners, banner => banner.Title == "Session pending");
        Assert.Contains(state.Banners, banner => banner.Title == "Transcript preview");
        Assert.Empty(state.Messages);
    }

    [Fact]
    public void SendDraft_GroupsConsecutiveOutgoingMessages()
    {
        var times = new Queue<DateTimeOffset>(
        [
            new DateTimeOffset(2026, 03, 25, 12, 00, 00, TimeSpan.Zero),
            new DateTimeOffset(2026, 03, 25, 12, 01, 00, TimeSpan.Zero)
        ]);

        var controller = new SessionTranscriptController(() => times.Dequeue());
        var snapshot = CreateSnapshot(SessionState.Running, SessionActivationSource.Broker);
        var connectionStatus = new MessageConnectionStatus(
            MessageConnectionState.Ready,
            "Preview mode",
            "squadscout",
            SupportsLiveSessionStream: false);

        controller.Sync(snapshot, connectionStatus);
        controller.SendDraft(snapshot, connectionStatus, "Ryan", "First message");
        var result = controller.SendDraft(snapshot, connectionStatus, "Ryan", "Second message");

        Assert.True(result.Success);
        Assert.Equal(2, result.ViewState.Messages.Count);
        Assert.False(result.ViewState.Messages[0].ShowTimestamp);
        Assert.False(result.ViewState.Messages[1].ShowSpeakerLabel);
        Assert.True(result.ViewState.Messages[1].UseCompactTopSpacing);
        Assert.All(result.ViewState.Messages, message => Assert.True(message.IsOutgoing));
    }

    [Fact]
    public void Sync_AppendsLifecycleMessageWhenSessionStateChanges()
    {
        var times = new Queue<DateTimeOffset>(
        [
            new DateTimeOffset(2026, 03, 25, 12, 00, 00, TimeSpan.Zero)
        ]);

        var controller = new SessionTranscriptController(() => times.Dequeue());
        var connectionStatus = new MessageConnectionStatus(
            MessageConnectionState.Ready,
            "Preview mode",
            "squadscout",
            SupportsLiveSessionStream: false);

        controller.Sync(CreateSnapshot(SessionState.Pending, SessionActivationSource.Broker), connectionStatus);
        var nextState = controller.Sync(CreateSnapshot(SessionState.Running, SessionActivationSource.Broker), connectionStatus);

        var lifecycleMessage = Assert.Single(nextState.Messages);
        Assert.True(lifecycleMessage.IsSystem);
        Assert.Contains("running", lifecycleMessage.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SendDraft_RejectsStoppedSessions()
    {
        var controller = new SessionTranscriptController();
        var snapshot = CreateSnapshot(SessionState.Stopped, SessionActivationSource.Broker);
        var connectionStatus = new MessageConnectionStatus(
            MessageConnectionState.Ready,
            "Preview mode",
            "squadscout",
            SupportsLiveSessionStream: false);

        controller.Sync(snapshot, connectionStatus);
        var result = controller.SendDraft(snapshot, connectionStatus, "Ryan", "Should fail");

        Assert.False(result.Success);
        Assert.Contains("stopped", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.ViewState.Messages);
    }

    private static ActiveSessionSnapshot CreateSnapshot(SessionState state, SessionActivationSource source)
    {
        return new ActiveSessionSnapshot(
            new RegisteredProject
            {
                ProjectId = "squadscout",
                DisplayName = "SquadScout",
                RepositoryRoot = @"D:\GitHub\SquadScout-10"
            },
            new SessionDescriptor
            {
                SessionId = "session-10",
                ProjectId = "squadscout",
                State = state,
                CreatedAtUtc = new DateTimeOffset(2026, 03, 25, 11, 30, 00, TimeSpan.Zero)
            },
            source,
            "Active session");
    }
}
