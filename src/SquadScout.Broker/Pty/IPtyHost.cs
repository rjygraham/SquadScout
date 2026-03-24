namespace SquadScout.Broker.Pty;

public interface IPtyHost
{
    Task<IPtySession> StartSessionAsync(PtySessionStartRequest request, CancellationToken cancellationToken = default);
}
