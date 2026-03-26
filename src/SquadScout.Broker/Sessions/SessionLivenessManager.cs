using System.Collections.Concurrent;
using System.Security.Cryptography;
using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Sessions;

public sealed class SessionLivenessManager : ISessionLivenessManager
{
    private readonly ConcurrentDictionary<string, SessionLivenessState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _senderInstanceId;
    private readonly TimeProvider _timeProvider;

    public SessionLivenessManager()
        : this(TimeProvider.System)
    {
    }

    public SessionLivenessManager(
        TimeProvider timeProvider,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? livenessTimeout = null,
        string? senderInstanceId = null)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        HeartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(SessionHeartbeatDefaults.ExpectedIntervalSeconds);
        LivenessTimeout = livenessTimeout ?? TimeSpan.FromSeconds(SessionHeartbeatDefaults.LivenessTimeoutSeconds);
        if (HeartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval), "Heartbeat interval must be positive.");
        }

        if (LivenessTimeout < HeartbeatInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(livenessTimeout), "Liveness timeout must be greater than or equal to the heartbeat interval.");
        }

        _senderInstanceId = string.IsNullOrWhiteSpace(senderInstanceId)
            ? Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? $"broker-{Environment.ProcessId}-{Guid.NewGuid():n}"
            : senderInstanceId;
    }

    public TimeSpan HeartbeatInterval { get; }

    public TimeSpan LivenessTimeout { get; }

    public void RegisterSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var now = _timeProvider.GetUtcNow();
        _sessions.AddOrUpdate(
            sessionId,
            _ => new SessionLivenessState(now),
            (_, existing) =>
            {
                lock (existing.SyncRoot)
                {
                    existing.LastClientActivityUtc = now;
                    existing.OutstandingNonces.Clear();
                }

                return existing;
            });
    }

    public void UnregisterSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _sessions.TryRemove(sessionId, out _);
    }

    public HeartbeatPayload IssueHeartbeat(string sessionId)
    {
        var state = GetRequiredState(sessionId);
        var now = _timeProvider.GetUtcNow();
        var expiresAtUtc = now + LivenessTimeout;
        var nonce = CreateNonce();

        lock (state.SyncRoot)
        {
            PruneExpiredNoncesLocked(state, now);
            state.OutstandingNonces[nonce] = expiresAtUtc;
        }

        return new HeartbeatPayload
        {
            ExpectedIntervalSeconds = Math.Max(1, (int)Math.Round(HeartbeatInterval.TotalSeconds)),
            LivenessTimeoutSeconds = Math.Max(1, (int)Math.Round(LivenessTimeout.TotalSeconds)),
            SenderInstanceId = _senderInstanceId,
            Nonce = nonce
        };
    }

    public bool CanAcceptHeartbeat(string sessionId, HeartbeatPayload payload, out string validationError)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            validationError = $"Session '{sessionId}' is not tracking broker heartbeats.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.AcknowledgedNonce))
        {
            validationError = "Heartbeat acknowledgements must echo the broker nonce.";
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        lock (state.SyncRoot)
        {
            PruneExpiredNoncesLocked(state, now);
            if (!state.OutstandingNonces.ContainsKey(payload.AcknowledgedNonce))
            {
                validationError = "Heartbeat acknowledgement nonce is stale, unknown, or already consumed.";
                return false;
            }
        }

        validationError = string.Empty;
        return true;
    }

    public bool TryCommitHeartbeat(string sessionId, HeartbeatPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!_sessions.TryGetValue(sessionId, out var state) || string.IsNullOrWhiteSpace(payload.AcknowledgedNonce))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        lock (state.SyncRoot)
        {
            PruneExpiredNoncesLocked(state, now);
            if (!state.OutstandingNonces.Remove(payload.AcknowledgedNonce))
            {
                return false;
            }

            state.LastClientActivityUtc = now;
            return true;
        }
    }

    public void RecordClientActivity(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        lock (state.SyncRoot)
        {
            state.LastClientActivityUtc = now;
            PruneExpiredNoncesLocked(state, now);
        }
    }

    public bool HasTimedOut(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        lock (state.SyncRoot)
        {
            PruneExpiredNoncesLocked(state, now);
            return now - state.LastClientActivityUtc >= LivenessTimeout;
        }
    }

    private SessionLivenessState GetRequiredState(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_sessions.TryGetValue(sessionId, out var state))
        {
            return state;
        }

        throw new KeyNotFoundException($"Session '{sessionId}' is not tracking broker heartbeats.");
    }

    private static string CreateNonce()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer);
    }

    private static void PruneExpiredNoncesLocked(SessionLivenessState state, DateTimeOffset now)
    {
        if (state.OutstandingNonces.Count == 0)
        {
            return;
        }

        foreach (var expiredNonce in state.OutstandingNonces
                     .Where(pair => pair.Value <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            state.OutstandingNonces.Remove(expiredNonce);
        }
    }

    private sealed class SessionLivenessState
    {
        public SessionLivenessState(DateTimeOffset lastClientActivityUtc)
        {
            LastClientActivityUtc = lastClientActivityUtc;
        }

        public object SyncRoot { get; } = new();

        public Dictionary<string, DateTimeOffset> OutstandingNonces { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset LastClientActivityUtc { get; set; }
    }
}
