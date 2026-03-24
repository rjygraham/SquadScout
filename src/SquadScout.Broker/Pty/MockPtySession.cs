using System.Threading.Channels;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Pty;

public sealed class MockPtySession : IPtySession
{
    private readonly Channel<PtySessionEvent> _events = Channel.CreateUnbounded<PtySessionEvent>();
    private readonly PriorityQueue<ScheduledEvent, (int DueTick, long Order)> _scheduledEvents = new();
    private readonly object _syncRoot = new();
    private readonly List<string> _writtenInputs = [];
    private bool _disposed;
    private bool _exitPublished;
    private int _currentTick;
    private long _nextEventOrder;

    public MockPtySession(PtySessionStartRequest request)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        SessionId = request.SessionId;
        ProjectId = request.ProjectId;
        State = SessionState.Running;
        PublishEventLocked(PtySessionEvent.Started());
    }

    public PtySessionStartRequest Request { get; }

    public string SessionId { get; }

    public string ProjectId { get; }

    public SessionState State { get; private set; }

    public int CurrentTick
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentTick;
            }
        }
    }

    public int PendingScheduledEventCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _scheduledEvents.Count;
            }
        }
    }

    public IReadOnlyList<string> WrittenInputs
    {
        get
        {
            lock (_syncRoot)
            {
                return _writtenInputs.ToArray();
            }
        }
    }

    public void EnqueueOutput(string content, int afterTicks = 0, bool isError = false, DateTimeOffset? timestampUtc = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        Schedule(PtySessionEvent.Output(content, isError, timestampUtc), afterTicks);
    }

    public void EnqueueExit(int? exitCode = 0, int afterTicks = 0, DateTimeOffset? timestampUtc = null) =>
        Schedule(PtySessionEvent.Exited(exitCode, timestampUtc), afterTicks);

    public int AdvanceBy(int ticks)
    {
        if (ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "Advance ticks must be non-negative.");
        }

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            _currentTick += ticks;
            return ReleaseDueEventsLocked();
        }
    }

    public bool ReleaseNext()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (!_scheduledEvents.TryPeek(out _, out var due))
            {
                return false;
            }

            _currentTick = Math.Max(_currentTick, due.DueTick);
            ReleaseNextScheduledEventLocked();
            return true;
        }
    }

    public int ReleaseAll()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            var released = 0;
            while (_scheduledEvents.Count > 0)
            {
                ReleaseNextScheduledEventLocked();
                released++;
            }

            return released;
        }
    }

    public Task WriteAsync(string input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(input);

        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (State == SessionState.Stopped)
            {
                throw new InvalidOperationException("Cannot write to a stopped PTY session.");
            }

            _writtenInputs.Add(input);
        }

        return Task.CompletedTask;
    }

    public bool TryReadEvent(out PtySessionEvent @event) => _events.Reader.TryRead(out @event);

    public ValueTask<PtySessionEvent> ReadEventAsync(CancellationToken cancellationToken = default) =>
        _events.Reader.ReadAsync(cancellationToken);

    public Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            if (_disposed || State == SessionState.Stopped)
            {
                return Task.CompletedTask;
            }

            _scheduledEvents.Clear();
            PublishExitLocked(null);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await TerminateAsync().ConfigureAwait(false);

        lock (_syncRoot)
        {
            _disposed = true;
            _events.Writer.TryComplete();
        }
    }

    private void Schedule(PtySessionEvent @event, int afterTicks)
    {
        if (afterTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterTicks), afterTicks, "Scheduled ticks must be non-negative.");
        }

        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (_exitPublished)
            {
                throw new InvalidOperationException("Cannot schedule PTY events after the session has exited.");
            }

            var dueTick = checked(_currentTick + afterTicks);
            _scheduledEvents.Enqueue(new ScheduledEvent(@event), (dueTick, _nextEventOrder++));
        }
    }

    private int ReleaseDueEventsLocked()
    {
        var released = 0;
        while (_scheduledEvents.TryPeek(out _, out var due) && due.DueTick <= _currentTick)
        {
            ReleaseNextScheduledEventLocked();
            released++;
        }

        return released;
    }

    private void ReleaseNextScheduledEventLocked()
    {
        var scheduledEvent = _scheduledEvents.Dequeue();
        switch (scheduledEvent.Event.Kind)
        {
            case PtySessionEventKind.Output:
                PublishEventLocked(scheduledEvent.Event);
                break;
            case PtySessionEventKind.Exited:
                PublishExitLocked(scheduledEvent.Event.ExitCode);
                break;
            default:
                PublishEventLocked(scheduledEvent.Event);
                break;
        }
    }

    private void PublishExitLocked(int? exitCode)
    {
        if (_exitPublished)
        {
            return;
        }

        State = SessionState.Stopped;
        _exitPublished = true;
        PublishEventLocked(PtySessionEvent.Exited(exitCode));
        _events.Writer.TryComplete();
    }

    private void PublishEventLocked(PtySessionEvent @event)
    {
        _events.Writer.TryWrite(@event);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record ScheduledEvent(PtySessionEvent Event);
}
