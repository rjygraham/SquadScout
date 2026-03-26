using System.Text.Json;
using SquadScout.App.Services;
using SquadScout.App.ViewModels;
using SquadScout.Contracts.Messages;
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
            new MessageConnectionStatus
            {
                State = MessageConnectionState.Ready,
                Summary = "Messaging composition is ready for the session.",
                Hub = "squadscout",
                SupportsLiveSessionStream = false
            });

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
        var connectionStatus = new MessageConnectionStatus
        {
            State = MessageConnectionState.Ready,
            Summary = "Preview mode",
            Hub = "squadscout",
            SupportsLiveSessionStream = false
        };

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
        var connectionStatus = new MessageConnectionStatus
        {
            State = MessageConnectionState.Ready,
            Summary = "Preview mode",
            Hub = "squadscout",
            SupportsLiveSessionStream = false
        };

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
        var connectionStatus = new MessageConnectionStatus
        {
            State = MessageConnectionState.Ready,
            Summary = "Preview mode",
            Hub = "squadscout",
            SupportsLiveSessionStream = false
        };

        controller.Sync(snapshot, connectionStatus);
        var result = controller.SendDraft(snapshot, connectionStatus, "Ryan", "Should fail");

        Assert.False(result.Success);
        Assert.Contains("stopped", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.ViewState.Messages);
    }

    [Fact]
    public void Sync_LivePendingSession_DisablesComposerUntilRunning()
    {
        var controller = new SessionTranscriptController();

        var state = controller.Sync(
            CreateSnapshot(SessionState.Pending, SessionActivationSource.Broker),
            new MessageConnectionStatus
            {
                State = MessageConnectionState.Connected,
                Summary = "Live messaging connected for the pending session.",
                Hub = "squadscout",
                SupportsLiveSessionStream = true
            });

        Assert.False(state.CanCompose);
        Assert.Equal("Wait for the broker to start the session before sending messages.", state.ComposerPlaceholder);
        Assert.Equal(
            "The broker is still starting this session. Messaging unlocks once the live session is running.",
            state.EmptyDescription);
    }

    [Fact]
    public void SendDraft_RejectsWhenLiveTransportIsUnavailable()
    {
        var controller = new SessionTranscriptController();
        var snapshot = CreateSnapshot(SessionState.Running, SessionActivationSource.Broker);
        var connectionStatus = new MessageConnectionStatus
        {
            State = MessageConnectionState.Faulted,
            Summary = "Reconnect failed.",
            FailureReason = "Socket unavailable.",
            Hub = "squadscout",
            SupportsLiveSessionStream = true
        };

        controller.Sync(snapshot, connectionStatus);
        var result = controller.SendDraft(snapshot, connectionStatus, "Ryan", "Should fail");

        Assert.False(result.Success);
        Assert.Contains("unavailable", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.ViewState.Messages);
        Assert.False(result.ViewState.CanCompose);
        Assert.Contains(result.ViewState.Banners, banner => banner.Title == "Live transport unavailable");
    }

    [Fact]
    public void ObserveTraffic_ReplayRecoveryAppendsSystemMessagesAndBrokerOutput()
    {
        var controller = new SessionTranscriptController();
        var snapshot = CreateSnapshot(SessionState.Running, SessionActivationSource.Broker);
        var connectionStatus = new MessageConnectionStatus
        {
            State = MessageConnectionState.Connected,
            Summary = "Connected.",
            Hub = "squadscout",
            SupportsLiveSessionStream = true
        };

        controller.Sync(snapshot, connectionStatus);
        controller.ObserveTraffic(snapshot, connectionStatus, CreateTraffic(CreateReplayRequest(ReplayRequestReason.ClientRecovery, 4)));
        var state = controller.ObserveTraffic(snapshot, connectionStatus, CreateTraffic(CreateOutputEnvelope(sequence: 4, content: "Recovered output")));

        Assert.Contains(state.Messages, message => message.IsSystem && message.Text.Contains("Recovering transcript messages from sequence 4", StringComparison.Ordinal));
        Assert.Contains(state.Messages, message => !message.IsSystem && message.Text == "Recovered output");
    }

    [Fact]
    public void RestoreFromTraffic_DeduplicatesPreviouslyPersistedReplayableMessages()
    {
        var controller = new SessionTranscriptController();
        var snapshot = CreateSnapshot(SessionState.Running, SessionActivationSource.Broker);
        var connectionStatus = new MessageConnectionStatus
        {
            State = MessageConnectionState.Connected,
            Summary = "Connected.",
            Hub = "squadscout",
            SupportsLiveSessionStream = true
        };

        var traffic = CreateTraffic(CreateOutputEnvelope(sequence: 8, content: "Only once"));
        controller.RestoreFromTraffic(snapshot, connectionStatus, [traffic]);
        var state = controller.ObserveTraffic(snapshot, connectionStatus, traffic);

        Assert.Single(state.Messages);
        Assert.Equal("Only once", state.Messages[0].Text);
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

    private static MessageEnvelopeTraffic CreateTraffic<TPayload>(MessageEnvelope<TPayload> envelope) =>
        new()
        {
            Direction = envelope.Direction == MessageDirection.BrokerToClient
                ? MessageTrafficDirection.Incoming
                : MessageTrafficDirection.Outgoing,
            Envelope = ToJsonEnvelope(envelope),
            Summary = envelope.MessageType.ToString()
        };

    private static MessageEnvelope<ReplayRequestPayload> CreateReplayRequest(ReplayRequestReason reason, long fromSequenceInclusive) =>
        new()
        {
            ProjectId = "squadscout",
            SessionId = "session-10",
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.ReplayRequest,
            Direction = MessageDirection.ClientToBroker,
            TimestampUtc = DateTimeOffset.UtcNow,
            MessageId = $"replay-{fromSequenceInclusive}",
            CorrelationId = "corr",
            Payload = new ReplayRequestPayload
            {
                FromSequenceInclusive = fromSequenceInclusive,
                Reason = reason
            }
        };

    private static MessageEnvelope<OutputChunkPayload> CreateOutputEnvelope(long sequence, string content) =>
        new()
        {
            ProjectId = "squadscout",
            SessionId = "session-10",
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.Output,
            Direction = MessageDirection.BrokerToClient,
            Sequence = sequence,
            TimestampUtc = DateTimeOffset.UtcNow,
            MessageId = $"broker-{sequence}",
            CorrelationId = "corr",
            Payload = new OutputChunkPayload
            {
                Content = content
            }
        };

    private static MessageEnvelope<JsonElement> ToJsonEnvelope<TPayload>(MessageEnvelope<TPayload> envelope) =>
        new()
        {
            ContractVersion = envelope.ContractVersion,
            ProjectId = envelope.ProjectId,
            SessionId = envelope.SessionId,
            Generation = envelope.Generation,
            MessageType = envelope.MessageType,
            Direction = envelope.Direction,
            Sequence = envelope.Sequence,
            ClientSequence = envelope.ClientSequence,
            AcknowledgedSequence = envelope.AcknowledgedSequence,
            TimestampUtc = envelope.TimestampUtc,
            MessageId = envelope.MessageId,
            CorrelationId = envelope.CorrelationId,
            CausationId = envelope.CausationId,
            Payload = JsonSerializer.SerializeToElement(envelope.Payload, SessionMessageSerializer.DefaultOptions)
        };
}
