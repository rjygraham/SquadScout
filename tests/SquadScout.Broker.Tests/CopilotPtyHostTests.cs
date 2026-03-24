using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SquadScout.Broker.Configuration;
using SquadScout.Broker.Pty;
using SquadScout.Broker.Sessions;
using SquadScout.Broker.Tests.TestDoubles;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class CopilotPtyHostTests
{
    [Fact]
    public async Task DirectSpawnEmitsStartedChunkedOutputAndExitCode()
    {
        var host = CreateHost(new CopilotPtyHostOptions
        {
            ExecutablePath = "powershell.exe",
            OutputBufferSize = 3
        });

        await using var session = await host.StartSessionAsync(new PtySessionStartRequest
        {
            ProjectId = "broker",
            SessionId = "session-123",
            WorkingDirectory = AppContext.BaseDirectory,
            Arguments =
            [
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "[Console]::Out.Write('hello'); exit 17"
            ]
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var events = await ReadUntilExitAsync(session, timeout.Token);

        Assert.Equal(PtySessionEventKind.Started, events[0].Kind);
        Assert.Equal(SessionState.Stopped, session.State);

        var outputChunks = events
            .Where(@event => @event.Kind == PtySessionEventKind.Output)
            .Select(@event => @event.Content ?? string.Empty)
            .ToArray();

        Assert.True(outputChunks.Length >= 2);
        Assert.Contains("hello", StripAnsi(string.Concat(outputChunks)), StringComparison.Ordinal);

        var exit = Assert.Single(events, @event => @event.Kind == PtySessionEventKind.Exited);
        Assert.Equal(17, exit.ExitCode);
    }

    [Fact]
    public async Task StartSessionAsyncSurfacesStartupFailures()
    {
        var host = CreateHost(new CopilotPtyHostOptions
        {
            ExecutablePath = "missing-copilot-binary.exe"
        });

        var exception = await Assert.ThrowsAsync<PtySessionStartException>(() => host.StartSessionAsync(new PtySessionStartRequest
        {
            ProjectId = "broker",
            SessionId = "session-123",
            WorkingDirectory = AppContext.BaseDirectory
        }));

        Assert.Equal("session-123", exception.SessionId);
        Assert.Equal("broker", exception.ProjectId);
        Assert.Equal("missing-copilot-binary.exe", exception.ExecutablePath);
    }

    [Fact]
    public async Task StartSessionAsyncHonorsCancellationBeforeSpawn()
    {
        var host = CreateHost();
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartSessionAsync(
            new PtySessionStartRequest
            {
                ProjectId = "broker",
                SessionId = "session-123",
                WorkingDirectory = AppContext.BaseDirectory
            },
            cancellationSource.Token));
    }

    [Fact]
    public async Task TerminateAsyncIsIdempotentAndReportsBrokerTearDown()
    {
        var host = CreateHost();
        await using var session = await host.StartSessionAsync(new PtySessionStartRequest
        {
            ProjectId = "broker",
            SessionId = "session-123",
            WorkingDirectory = AppContext.BaseDirectory,
            Arguments =
            [
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "Start-Sleep -Seconds 30"
            ]
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var started = await session.ReadEventAsync(timeout.Token);
        Assert.Equal(PtySessionEventKind.Started, started.Kind);

        await session.TerminateAsync(timeout.Token);
        await session.TerminateAsync(timeout.Token);

        var remainingEvents = await ReadUntilExitAsync(session, timeout.Token);
        var exited = Assert.Single(remainingEvents, @event => @event.Kind == PtySessionEventKind.Exited);
        Assert.Equal(PtySessionEventKind.Exited, exited.Kind);
        Assert.Null(exited.ExitCode);
        Assert.Equal(SessionState.Stopped, session.State);
    }

    [Fact]
    public async Task PumpsRealPtyEventsThroughEnvelopePipeline()
    {
        var relayPublisher = new RecordingRelayPublisher();
        var orchestrator = new InMemorySessionOrchestrator(relayPublisher, new SessionSequenceValidator());
        var pump = new PtySessionEnvelopePump(orchestrator);
        var sessionDescriptor = await orchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        var host = CreateHost(new CopilotPtyHostOptions
        {
            ExecutablePath = "powershell.exe",
            OutputBufferSize = 3
        });

        await using var session = await host.StartSessionAsync(new PtySessionStartRequest
        {
            ProjectId = sessionDescriptor.ProjectId,
            SessionId = sessionDescriptor.SessionId,
            WorkingDirectory = AppContext.BaseDirectory,
            Arguments =
            [
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "[Console]::Out.Write('hello'); exit 0"
            ]
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await pump.PumpUntilExitAsync(session, timeout.Token);

        var published = relayPublisher.PublishedEnvelopes;
        Assert.True(published.Count >= 3);

        var started = published[0];
        Assert.Equal(1, started.Sequence);
        Assert.Equal(SessionMessageType.SessionLifecycle, started.MessageType);
        var startedPayload = MockPtyHarnessFixture.DeserializePayload<SessionLifecyclePayload>(started);
        Assert.Equal(SessionState.Running, startedPayload.State);
        Assert.Equal("pty-started", startedPayload.Reason);

        var stopped = published[^1];
        Assert.Equal(SessionMessageType.SessionLifecycle, stopped.MessageType);
        var stoppedPayload = MockPtyHarnessFixture.DeserializePayload<SessionLifecyclePayload>(stopped);
        Assert.Equal(SessionState.Stopped, stoppedPayload.State);
        Assert.Equal(0, stoppedPayload.ExitCode);

        var output = string.Concat(
            published
                .Where(message => message.MessageType == SessionMessageType.Output)
                .Select(message => MockPtyHarnessFixture.DeserializePayload<OutputChunkPayload>(message).Content));

        Assert.Contains("hello", StripAnsi(output), StringComparison.Ordinal);

        var expectedSequence = 1L;
        foreach (var message in published)
        {
            Assert.Equal(expectedSequence++, message.Sequence);
        }

        var descriptor = await orchestrator.GetAsync(sessionDescriptor.SessionId);
        Assert.NotNull(descriptor);
        Assert.Equal(SessionState.Stopped, descriptor!.State);
    }

    private static CopilotPtyHost CreateHost(CopilotPtyHostOptions? options = null) =>
        new(
            Options.Create(options ?? new CopilotPtyHostOptions
            {
                ExecutablePath = "powershell.exe",
                OutputBufferSize = 3
            }),
            NullLoggerFactory.Instance,
            NullLogger<CopilotPtyHost>.Instance);

    private static async Task<IReadOnlyList<PtySessionEvent>> ReadUntilExitAsync(IPtySession session, CancellationToken cancellationToken)
    {
        var events = new List<PtySessionEvent>();
        while (true)
        {
            var @event = await session.ReadEventAsync(cancellationToken);
            events.Add(@event);

            if (@event.Kind == PtySessionEventKind.Exited)
            {
                return events;
            }
        }
    }

    private static string StripAnsi(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current != '\u001B')
            {
                builder.Append(current);
                continue;
            }

            if (index + 1 < value.Length && value[index + 1] == '[')
            {
                index++;
                while (index + 1 < value.Length)
                {
                    index++;
                    if (value[index] is >= '@' and <= '~')
                    {
                        break;
                    }
                }
            }
        }

        return builder.ToString();
    }
}
