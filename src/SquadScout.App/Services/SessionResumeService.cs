using System.Text.Json;
using SquadScout.Contracts.Messages;

namespace SquadScout.App.Services;

public sealed class SessionResumeService : ISessionResumeService
{
    private readonly IActiveSessionState _activeSessionState;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _storagePath;

    private ActiveSessionResumeState? _currentState;
    private bool _restored;

    public SessionResumeService(string storagePath, IActiveSessionState activeSessionState)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            throw new ArgumentException("A storage path is required.", nameof(storagePath));
        }

        _storagePath = storagePath;
        _activeSessionState = activeSessionState ?? throw new ArgumentNullException(nameof(activeSessionState));
    }

    public ActiveSessionResumeState? CurrentState => _currentState;

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_restored)
            {
                return;
            }

            _restored = true;
            _currentState = await ReadStateCoreAsync(cancellationToken).ConfigureAwait(false);

            if (_currentState?.Snapshot is not { HasActiveSession: true, Project: not null, Session: not null } snapshot)
            {
                return;
            }

            var restoredSummary = BuildRestoredSummary(snapshot, _currentState.SavedAtUtc);
            var restoredSnapshot = snapshot with { Summary = restoredSummary };
            _currentState = _currentState with { Snapshot = restoredSnapshot };
            _activeSessionState.SetActiveSession(
                restoredSnapshot.Project!,
                restoredSnapshot.Session!,
                restoredSnapshot.Source,
                restoredSnapshot.Summary);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ActiveSessionResumeState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        if (!state.Snapshot.HasActiveSession)
        {
            await ClearAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _restored = true;
            _currentState = state with { SavedAtUtc = DateTimeOffset.UtcNow };

            var directory = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = File.Create(_storagePath);
            await JsonSerializer.SerializeAsync(stream, _currentState, SessionMessageSerializer.DefaultOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _restored = true;
            _currentState = null;
            if (File.Exists(_storagePath))
            {
                File.Delete(_storagePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ActiveSessionResumeState?> ReadStateCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storagePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_storagePath);
            return await JsonSerializer.DeserializeAsync<ActiveSessionResumeState>(
                    stream,
                    SessionMessageSerializer.DefaultOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            File.Delete(_storagePath);
            return null;
        }
    }

    private static string BuildRestoredSummary(ActiveSessionSnapshot snapshot, DateTimeOffset savedAtUtc)
    {
        var sourceSummary = snapshot.Source == SessionActivationSource.Broker
            ? "Resume it to reconnect and replay any missing transcript messages."
            : "Resume it to keep reading the native transcript from this device.";
        var savedAtLocal = savedAtUtc.ToLocalTime().ToString("g");

        return $"Recovered '{snapshot.Project?.DisplayName ?? "session"}' from this device (saved {savedAtLocal}). {sourceSummary}";
    }
}
