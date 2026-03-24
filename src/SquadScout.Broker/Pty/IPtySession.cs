using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Pty;

public interface IPtySession : IAsyncDisposable
{
    string SessionId { get; }

    string ProjectId { get; }

    SessionState State { get; }

    Task WriteAsync(string input, CancellationToken cancellationToken = default);

    bool TryReadEvent(out PtySessionEvent @event);

    ValueTask<PtySessionEvent> ReadEventAsync(CancellationToken cancellationToken = default);

    Task TerminateAsync(CancellationToken cancellationToken = default);
}
