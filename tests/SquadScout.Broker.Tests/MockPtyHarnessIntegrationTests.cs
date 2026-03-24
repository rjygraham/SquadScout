using SquadScout.Broker.Tests.TestDoubles;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class MockPtyHarnessIntegrationTests
{
    [Fact]
    public async Task PumpsDeterministicPtyEventsThroughRelaySequencingAndReplay()
    {
        var harness = new MockPtyHarnessFixture(replayBufferCapacity: 8);
        var session = await harness.StartAsync("--project", "broker");

        await harness.PtySession.WriteAsync("explain testability\n");
        harness.PtySession.EnqueueOutput("hel", afterTicks: 1);
        harness.PtySession.EnqueueOutput("lo", afterTicks: 2);
        harness.PtySession.EnqueueExit(0, afterTicks: 3);

        Assert.Equal(["explain testability\n"], harness.PtySession.WrittenInputs);

        harness.PtySession.AdvanceBy(1);
        await harness.PumpAvailableAsync();

        harness.PtySession.AdvanceBy(2);
        await harness.PumpAvailableAsync();

        var published = harness.RelayPublisher.PublishedEnvelopes;
        Assert.Single(harness.RelayPublisher.StartedSessions);
        Assert.Equal(session.SessionId, harness.RelayPublisher.StartedSessions[0].SessionId);

        Assert.Collection(
            published,
            message =>
            {
                Assert.Equal(1, message.Sequence);
                Assert.Equal(SessionMessageType.SessionLifecycle, message.MessageType);
                var payload = MockPtyHarnessFixture.DeserializePayload<SessionLifecyclePayload>(message);
                Assert.Equal(SessionState.Running, payload.State);
                Assert.Equal("pty-started", payload.Reason);
            },
            message =>
            {
                Assert.Equal(2, message.Sequence);
                Assert.Equal(SessionMessageType.Output, message.MessageType);
                var payload = MockPtyHarnessFixture.DeserializePayload<OutputChunkPayload>(message);
                Assert.Equal("hel", payload.Content);
                Assert.False(payload.IsError);
            },
            message =>
            {
                Assert.Equal(3, message.Sequence);
                Assert.Equal(SessionMessageType.Output, message.MessageType);
                var payload = MockPtyHarnessFixture.DeserializePayload<OutputChunkPayload>(message);
                Assert.Equal("lo", payload.Content);
                Assert.False(payload.IsError);
            },
            message =>
            {
                Assert.Equal(4, message.Sequence);
                Assert.Equal(SessionMessageType.SessionLifecycle, message.MessageType);
                var payload = MockPtyHarnessFixture.DeserializePayload<SessionLifecyclePayload>(message);
                Assert.Equal(SessionState.Stopped, payload.State);
                Assert.Equal(0, payload.ExitCode);
            });

        var replay = await harness.Orchestrator.ReplayAsync(session.SessionId, harness.CreateReplayRequest(fromSequenceInclusive: 1));
        Assert.Equal(1, replay.Payload.FromSequenceInclusive);
        Assert.Equal(4, replay.Payload.ToSequenceInclusive);
        Assert.False(replay.Payload.GapDetected);
        Assert.Equal(4, replay.Payload.Messages.Count);

        var descriptor = await harness.Orchestrator.GetAsync(session.SessionId);
        Assert.NotNull(descriptor);
        Assert.Equal(SessionState.Stopped, descriptor!.State);
    }
}
