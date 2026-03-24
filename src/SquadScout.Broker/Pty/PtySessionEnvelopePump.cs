using System.Threading;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Pty;

public sealed class PtySessionEnvelopePump
{
    private readonly ISessionOrchestrator _orchestrator;
    private long _nextMessageId;

    public PtySessionEnvelopePump(ISessionOrchestrator orchestrator)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    }

    public async Task<int> PumpAvailableAsync(IPtySession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var pumped = 0;
        while (session.TryReadEvent(out var @event))
        {
            await PublishEventAsync(session.SessionId, @event, cancellationToken).ConfigureAwait(false);
            pumped++;
        }

        return pumped;
    }

    public async Task PumpUntilExitAsync(IPtySession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        while (true)
        {
            var @event = await session.ReadEventAsync(cancellationToken).ConfigureAwait(false);
            await PublishEventAsync(session.SessionId, @event, cancellationToken).ConfigureAwait(false);

            if (@event.Kind == PtySessionEventKind.Exited)
            {
                return;
            }
        }
    }

    public Task PublishEventAsync(string sessionId, PtySessionEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(@event);

        return @event.Kind switch
        {
            PtySessionEventKind.Started => _orchestrator.RecordBrokerMessageAsync(
                sessionId,
                CreateBrokerCommand(
                    sessionId,
                    SessionMessageType.SessionLifecycle,
                    new SessionLifecyclePayload
                    {
                        State = SessionState.Running,
                        Reason = "pty-started"
                    },
                    @event.TimestampUtc),
                cancellationToken),

            PtySessionEventKind.Output => _orchestrator.RecordBrokerMessageAsync(
                sessionId,
                CreateBrokerCommand(
                    sessionId,
                    SessionMessageType.Output,
                    new OutputChunkPayload
                    {
                        Content = @event.Content ?? string.Empty,
                        IsError = @event.IsError
                    },
                    @event.TimestampUtc),
                cancellationToken),

            PtySessionEventKind.Exited => _orchestrator.RecordBrokerMessageAsync(
                sessionId,
                CreateBrokerCommand(
                    sessionId,
                    SessionMessageType.SessionLifecycle,
                    new SessionLifecyclePayload
                    {
                        State = SessionState.Stopped,
                        Reason = "pty-exited",
                        ExitCode = @event.ExitCode
                    },
                    @event.TimestampUtc),
                cancellationToken),

            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event.Kind, "Unsupported PTY event kind.")
        };
    }

    private BrokerEnvelopeCommand<TPayload> CreateBrokerCommand<TPayload>(
        string sessionId,
        SessionMessageType messageType,
        TPayload payload,
        DateTimeOffset timestampUtc) =>
        new()
        {
            MessageType = messageType,
            MessageId = $"pty-{sessionId}-{Interlocked.Increment(ref _nextMessageId)}",
            CorrelationId = $"pty-session:{sessionId}",
            TimestampUtc = timestampUtc,
            Payload = payload
        };
}
