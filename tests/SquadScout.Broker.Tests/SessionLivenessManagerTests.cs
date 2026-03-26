using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Tests;

public sealed class SessionLivenessManagerTests
{
    [Fact]
    public void TryCommitHeartbeatConsumesOutstandingNonceAndRefreshesActivity()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 03, 26, 12, 00, 00, TimeSpan.Zero));
        var manager = new SessionLivenessManager(
            timeProvider,
            heartbeatInterval: TimeSpan.FromSeconds(10),
            livenessTimeout: TimeSpan.FromSeconds(30),
            senderInstanceId: "broker-tests");

        manager.RegisterSession("session-abc");
        var heartbeat = manager.IssueHeartbeat("session-abc");

        Assert.True(manager.CanAcceptHeartbeat("session-abc", new HeartbeatPayload
        {
            AcknowledgedNonce = heartbeat.Nonce
        }, out var validationError));
        Assert.True(string.IsNullOrEmpty(validationError));

        timeProvider.Advance(TimeSpan.FromSeconds(20));
        Assert.True(manager.TryCommitHeartbeat("session-abc", new HeartbeatPayload
        {
            AcknowledgedNonce = heartbeat.Nonce
        }));
        Assert.False(manager.HasTimedOut("session-abc"));

        Assert.False(manager.CanAcceptHeartbeat("session-abc", new HeartbeatPayload
        {
            AcknowledgedNonce = heartbeat.Nonce
        }, out validationError));
        Assert.Contains("stale", validationError, StringComparison.OrdinalIgnoreCase);

        timeProvider.Advance(TimeSpan.FromSeconds(29));
        Assert.False(manager.HasTimedOut("session-abc"));

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.True(manager.HasTimedOut("session-abc"));
    }

    [Fact]
    public void CanAcceptHeartbeatRejectsExpiredNonce()
    {
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 03, 26, 12, 00, 00, TimeSpan.Zero));
        var manager = new SessionLivenessManager(
            timeProvider,
            heartbeatInterval: TimeSpan.FromSeconds(5),
            livenessTimeout: TimeSpan.FromSeconds(15),
            senderInstanceId: "broker-tests");

        manager.RegisterSession("session-abc");
        var heartbeat = manager.IssueHeartbeat("session-abc");

        timeProvider.Advance(TimeSpan.FromSeconds(15));

        Assert.False(manager.CanAcceptHeartbeat("session-abc", new HeartbeatPayload
        {
            AcknowledgedNonce = heartbeat.Nonce
        }, out var validationError));
        Assert.Contains("stale", validationError, StringComparison.OrdinalIgnoreCase);
        Assert.False(manager.TryCommitHeartbeat("session-abc", new HeartbeatPayload
        {
            AcknowledgedNonce = heartbeat.Nonce
        }));
        Assert.True(manager.HasTimedOut("session-abc"));
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
