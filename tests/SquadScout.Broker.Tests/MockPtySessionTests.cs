using SquadScout.Broker.Pty;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class MockPtySessionTests
{
    [Fact]
    public async Task AdvancesChunkedOutputByLogicalTicks()
    {
        var session = new MockPtySession(new PtySessionStartRequest
        {
            ProjectId = "broker",
            SessionId = "session-123"
        });

        session.EnqueueOutput("hel", afterTicks: 1);
        session.EnqueueOutput("lo", afterTicks: 2);
        session.EnqueueExit(0, afterTicks: 3);

        var started = await session.ReadEventAsync();
        Assert.Equal(PtySessionEventKind.Started, started.Kind);
        Assert.Equal(SessionState.Running, session.State);

        Assert.Equal(1, session.AdvanceBy(1));
        Assert.True(session.TryReadEvent(out var firstChunk));
        Assert.Equal(PtySessionEventKind.Output, firstChunk.Kind);
        Assert.Equal("hel", firstChunk.Content);

        Assert.Equal(1, session.AdvanceBy(1));
        Assert.True(session.TryReadEvent(out var secondChunk));
        Assert.Equal("lo", secondChunk.Content);

        Assert.Equal(1, session.AdvanceBy(1));
        Assert.True(session.TryReadEvent(out var exited));
        Assert.Equal(PtySessionEventKind.Exited, exited.Kind);
        Assert.Equal(0, exited.ExitCode);
        Assert.Equal(SessionState.Stopped, session.State);
    }

    [Fact]
    public async Task ReleaseNextOverridesTimingWithoutWallClockDelays()
    {
        var session = new MockPtySession(new PtySessionStartRequest
        {
            ProjectId = "broker",
            SessionId = "session-123"
        });

        session.EnqueueOutput("later", afterTicks: 5);
        session.EnqueueExit(17, afterTicks: 6);

        _ = await session.ReadEventAsync();

        Assert.True(session.ReleaseNext());
        Assert.True(session.TryReadEvent(out var output));
        Assert.Equal("later", output.Content);
        Assert.Equal(5, session.CurrentTick);

        Assert.True(session.ReleaseNext());
        Assert.True(session.TryReadEvent(out var exit));
        Assert.Equal(17, exit.ExitCode);
        Assert.False(session.ReleaseNext());
    }

    [Fact]
    public async Task RecordsInputsAndRejectsWritesAfterExit()
    {
        var session = new MockPtySession(new PtySessionStartRequest
        {
            ProjectId = "broker",
            SessionId = "session-123"
        });

        _ = await session.ReadEventAsync();
        await session.WriteAsync("status --json\n");
        session.EnqueueExit(0);
        session.ReleaseNext();
        _ = await session.ReadEventAsync();

        Assert.Equal(["status --json\n"], session.WrittenInputs);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteAsync("after-exit\n"));
        Assert.Contains("stopped", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HonorsCancellationAndTerminationDeterministically()
    {
        var session = new MockPtySession(new PtySessionStartRequest
        {
            ProjectId = "broker",
            SessionId = "session-123"
        });

        _ = await session.ReadEventAsync();

        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadEventAsync(cancellationSource.Token).AsTask());

        await session.TerminateAsync();
        var exited = await session.ReadEventAsync();

        Assert.Equal(PtySessionEventKind.Exited, exited.Kind);
        Assert.Null(exited.ExitCode);
        Assert.Equal(SessionState.Stopped, session.State);
    }

    [Fact]
    public async Task HostCanInjectStartFailures()
    {
        var host = new MockPtyHost();
        host.FailNextStart(new InvalidOperationException("boom"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartSessionAsync(new PtySessionStartRequest
        {
            ProjectId = "broker",
            SessionId = "session-123"
        }));

        Assert.Contains("boom", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.StartRequests);
    }
}
