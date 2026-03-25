using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pty.Net;
using SquadScout.Contracts.Security;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Pty;

public sealed class CopilotPtySession : IPtySession
{
    private readonly IPtyConnection _connection;
    private readonly Channel<PtySessionEvent> _events = Channel.CreateUnbounded<PtySessionEvent>();
    private readonly CancellationTokenSource _lifecycleCancellation = new();
    private readonly ILogger<CopilotPtySession> _logger;
    private readonly int _maxInputCharactersPerWrite;
    private readonly StreamReader _reader;
    private readonly TaskCompletionSource<ProcessExitObservation> _processExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _outputPump;
    private readonly Task _exitMonitor;
    private readonly Task _completion;
    private readonly object _syncRoot = new();
    private Task _cleanup = Task.CompletedTask;
    private bool _cleanupStarted;
    private bool _disposeStarted;
    private bool _disposed;
    private bool _exitPublished;
    private bool _terminationRequested;

    public CopilotPtySession(
        PtySessionStartRequest request,
        IPtyConnection connection,
        int outputBufferSize,
        int maxInputCharactersPerWrite,
        ILogger<CopilotPtySession> logger)
    {
        ArgumentNullException.ThrowIfNull(request);
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxInputCharactersPerWrite = Math.Max(1, maxInputCharactersPerWrite);

        SessionId = request.SessionId;
        ProjectId = request.ProjectId;
        State = SessionState.Running;

        _reader = new StreamReader(
            _connection.ReaderStream,
            new UTF8Encoding(false, false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: Math.Max(1, outputBufferSize),
            leaveOpen: true);

        _events.Writer.TryWrite(PtySessionEvent.Started());
        _outputPump = Task.Run(() => PumpOutputAsync(Math.Max(1, outputBufferSize)));
        _exitMonitor = Task.Run(() => MonitorProcessExitAsync(_lifecycleCancellation.Token));
        _completion = Task.Run(CompleteSessionAsync);
    }

    public string SessionId { get; }

    public string ProjectId { get; }

    public SessionState State { get; private set; }

    public async Task WriteAsync(string input, CancellationToken cancellationToken = default)
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
        }

        var sanitizedInput = PtyInputSanitizer.Sanitize(input, _maxInputCharactersPerWrite);
        var buffer = Encoding.UTF8.GetBytes(sanitizedInput);
        await _connection.WriterStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await _connection.WriterStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool TryReadEvent(out PtySessionEvent @event)
    {
        if (_events.Reader.TryRead(out var nextEvent))
        {
            @event = nextEvent;
            return true;
        }

        @event = null!;
        return false;
    }

    public ValueTask<PtySessionEvent> ReadEventAsync(CancellationToken cancellationToken = default) =>
        _events.Reader.ReadAsync(cancellationToken);

    public async Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var shouldKill = false;
        lock (_syncRoot)
        {
            if (_disposed || State == SessionState.Stopped)
            {
                return;
            }

            if (!_terminationRequested)
            {
                _terminationRequested = true;
                shouldKill = true;
            }
        }

        if (shouldKill)
        {
            try
            {
                _logger.LogInformation("Terminating PTY session {SessionId}.", SessionId);
                _connection.Kill();
            }
            catch (ObjectDisposedException) when (IsTerminationComplete())
            {
            }
        }

        await _completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_syncRoot)
        {
            if (_disposeStarted)
            {
                return;
            }

            _disposeStarted = true;
        }

        try
        {
            await TerminateAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleCancellation.Cancel();
            var cleanup = StartCleanup();

            try
            {
                await _completion.ConfigureAwait(false);
            }
            catch
            {
            }

            await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            _lifecycleCancellation.Dispose();

            lock (_syncRoot)
            {
                _disposed = true;
            }
        }
    }

    private async Task PumpOutputAsync(int outputBufferSize)
    {
        var buffer = new char[outputBufferSize];

        try
        {
            while (true)
            {
                var read = await _reader.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var chunk = new string(buffer, 0, read);
                if (!string.IsNullOrEmpty(chunk))
                {
                    _events.Writer.TryWrite(PtySessionEvent.Output(chunk));
                }
            }
        }
        catch (ObjectDisposedException) when (_lifecycleCancellation.IsCancellationRequested || IsTerminationComplete())
        {
        }
        catch (IOException) when (_lifecycleCancellation.IsCancellationRequested || IsTerminationComplete())
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PTY output pump failed for session {SessionId}.", SessionId);
            _processExit.TrySetException(ex);
        }
    }

    private async Task MonitorProcessExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_connection.WaitForExit(250))
                {
                    _processExit.TrySetResult(new ProcessExitObservation(_connection.ExitCode, WasTerminationRequested()));
                    return;
                }

                await Task.Yield();
            }

            _processExit.TrySetResult(new ProcessExitObservation(null, WasTerminationRequested()));
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || IsTerminationComplete())
        {
            _processExit.TrySetResult(new ProcessExitObservation(null, WasTerminationRequested()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PTY exit monitor failed for session {SessionId}.", SessionId);
            _processExit.TrySetException(ex);
        }
    }

    private async Task CompleteSessionAsync()
    {
        int? exitCode = null;
        var terminatedByBroker = false;

        try
        {
            var processExit = await _processExit.Task.ConfigureAwait(false);
            exitCode = processExit.ExitCode;
            terminatedByBroker = processExit.TerminatedByBroker;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PTY session {SessionId} ended because exit monitoring failed.", SessionId);
        }

        try
        {
            if (await Task.WhenAny(_outputPump, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != _outputPump)
            {
                _logger.LogWarning("PTY session {SessionId} did not drain output within the timeout window.", SessionId);
                _lifecycleCancellation.Cancel();
                var forcedCleanup = StartCleanup();
                await Task.WhenAny(Task.WhenAll(_outputPump, forcedCleanup), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
            else
            {
                await _outputPump.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PTY session {SessionId} output drain failed during completion.", SessionId);
        }
        finally
        {
            _lifecycleCancellation.Cancel();

            try
            {
                await Task.WhenAny(_exitMonitor, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PTY session {SessionId} exit monitor faulted during completion cleanup.", SessionId);
            }

            var cleanup = StartCleanup();

            try
            {
                await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PTY session {SessionId} cleanup faulted during completion.", SessionId);
            }
        }

        PublishExit(terminatedByBroker ? null : exitCode);
    }

    private void PublishExit(int? exitCode)
    {
        lock (_syncRoot)
        {
            if (_exitPublished)
            {
                return;
            }

            State = SessionState.Stopped;
            _exitPublished = true;
            _events.Writer.TryWrite(PtySessionEvent.Exited(exitCode));
            _events.Writer.TryComplete();
        }
    }

    private bool IsTerminationComplete()
    {
        lock (_syncRoot)
        {
            return _terminationRequested || _disposed || _exitPublished;
        }
    }

    private bool WasTerminationRequested()
    {
        lock (_syncRoot)
        {
            return _terminationRequested;
        }
    }

    private Task StartCleanup()
    {
        lock (_syncRoot)
        {
            if (_cleanupStarted)
            {
                return _cleanup;
            }

            _cleanupStarted = true;
            _cleanup = Task.Run(() =>
            {
                try
                {
                    _reader.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Ignoring PTY reader cleanup failure for session {SessionId}.", SessionId);
                }

                try
                {
                    _connection.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PTY connection cleanup failed for session {SessionId}.", SessionId);
                }
            });

            return _cleanup;
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private sealed record ProcessExitObservation(int? ExitCode, bool TerminatedByBroker);
}
