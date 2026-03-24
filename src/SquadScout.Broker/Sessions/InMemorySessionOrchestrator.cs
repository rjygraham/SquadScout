using System.Collections.Concurrent;
using SquadScout.Broker.Relay;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Sessions;

public sealed class InMemorySessionOrchestrator : ISessionOrchestrator
{
    private readonly IRelayPublisher _relayPublisher;
    private readonly ConcurrentDictionary<string, SessionDescriptor> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public InMemorySessionOrchestrator(IRelayPublisher relayPublisher)
    {
        _relayPublisher = relayPublisher;
    }

    public Task<SessionDescriptor?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public async Task<SessionDescriptor> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.ProjectId))
        {
            throw new ArgumentException("A project id is required.", nameof(command));
        }

        var session = new SessionDescriptor
        {
            SessionId = Guid.NewGuid().ToString("n"),
            ProjectId = command.ProjectId,
            State = SessionState.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        _sessions[session.SessionId] = session;
        await _relayPublisher.PublishSessionStartedAsync(session, cancellationToken);

        return session;
    }
}
