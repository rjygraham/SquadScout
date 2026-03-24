using System.Collections.Concurrent;

namespace SquadScout.Broker.Pty;

public sealed class MockPtyHost : IPtyHost
{
    private readonly object _syncRoot = new();
    private readonly Queue<Exception> _startFailures = new();
    private readonly List<PtySessionStartRequest> _startRequests = [];
    private readonly ConcurrentDictionary<string, MockPtySession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PtySessionStartRequest> StartRequests
    {
        get
        {
            lock (_syncRoot)
            {
                return _startRequests.ToArray();
            }
        }
    }

    public void FailNextStart(Exception? exception = null)
    {
        lock (_syncRoot)
        {
            _startFailures.Enqueue(exception ?? new InvalidOperationException("The mock PTY host failed to start."));
        }
    }

    public MockPtySession GetRequiredSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            return session;
        }

        throw new KeyNotFoundException($"Mock PTY session '{sessionId}' was not found.");
    }

    public Task<IPtySession> StartSessionAsync(PtySessionStartRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            throw new ArgumentException("A session id is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            throw new ArgumentException("A project id is required.", nameof(request));
        }

        lock (_syncRoot)
        {
            if (_startFailures.TryDequeue(out var exception))
            {
                throw exception;
            }

            _startRequests.Add(request);
        }

        var session = new MockPtySession(request);
        if (!_sessions.TryAdd(session.SessionId, session))
        {
            throw new InvalidOperationException($"Mock PTY session '{session.SessionId}' already exists.");
        }

        return Task.FromResult<IPtySession>(session);
    }
}
