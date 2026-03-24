using System.Text.Json;
using SquadScout.Broker.Pty;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests.TestDoubles;

public sealed class MockPtyHarnessFixture
{
    private long _nextMessageId = 1;

    public MockPtyHarnessFixture(int replayBufferCapacity = SessionSequencingDefaults.ReplayBufferCapacity)
    {
        RelayPublisher = new RecordingRelayPublisher();
        Orchestrator = new InMemorySessionOrchestrator(RelayPublisher, new SessionSequenceValidator(), replayBufferCapacity);
        PtyHost = new MockPtyHost();
    }

    public RecordingRelayPublisher RelayPublisher { get; }

    public InMemorySessionOrchestrator Orchestrator { get; }

    public MockPtyHost PtyHost { get; }

    public SessionDescriptor Session { get; private set; } = default!;

    public MockPtySession PtySession { get; private set; } = default!;

    public async Task<SessionDescriptor> StartAsync(string projectId = "broker", params string[] arguments)
    {
        Session = await Orchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = projectId,
            RequestedBy = "tests",
            Arguments = arguments
        });

        PtySession = (MockPtySession)await PtyHost.StartSessionAsync(new PtySessionStartRequest
        {
            ProjectId = Session.ProjectId,
            SessionId = Session.SessionId,
            Arguments = arguments
        });

        await PumpAvailableAsync();
        return Session;
    }

    public async Task PumpAvailableAsync()
    {
        while (PtySession.TryReadEvent(out var @event))
        {
            switch (@event.Kind)
            {
                case PtySessionEventKind.Started:
                    await Orchestrator.RecordBrokerMessageAsync(
                        Session.SessionId,
                        CreateBrokerCommand(
                            SessionMessageType.SessionLifecycle,
                            new SessionLifecyclePayload
                            {
                                State = SessionState.Running,
                                Reason = "pty-started"
                            },
                            @event.TimestampUtc));
                    break;

                case PtySessionEventKind.Output:
                    await Orchestrator.RecordBrokerMessageAsync(
                        Session.SessionId,
                        CreateBrokerCommand(
                            SessionMessageType.Output,
                            new OutputChunkPayload
                            {
                                Content = @event.Content ?? string.Empty,
                                IsError = @event.IsError
                            },
                            @event.TimestampUtc));
                    break;

                case PtySessionEventKind.Exited:
                    await Orchestrator.RecordBrokerMessageAsync(
                        Session.SessionId,
                        CreateBrokerCommand(
                            SessionMessageType.SessionLifecycle,
                            new SessionLifecyclePayload
                            {
                                State = SessionState.Stopped,
                                Reason = "pty-exited",
                                ExitCode = @event.ExitCode
                            },
                            @event.TimestampUtc));
                    break;
            }
        }
    }

    public MessageEnvelope<ReplayRequestPayload> CreateReplayRequest(long fromSequenceInclusive, int maximumMessages = 100) =>
        new()
        {
            ProjectId = Session.ProjectId,
            SessionId = Session.SessionId,
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.ReplayRequest,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = 1,
            MessageId = $"client-replay-{_nextMessageId++}",
            CorrelationId = "corr-replay",
            Payload = new ReplayRequestPayload
            {
                FromSequenceInclusive = fromSequenceInclusive,
                MaximumMessages = maximumMessages,
                Reason = ReplayRequestReason.ReconnectResume
            }
        };

    private BrokerEnvelopeCommand<TPayload> CreateBrokerCommand<TPayload>(
        SessionMessageType messageType,
        TPayload payload,
        DateTimeOffset timestampUtc) =>
        new()
        {
            MessageType = messageType,
            MessageId = $"broker-{messageType.ToString().ToLowerInvariant()}-{_nextMessageId++}",
            CorrelationId = "corr-mock-pty",
            TimestampUtc = timestampUtc,
            Payload = payload
        };

    public static TPayload DeserializePayload<TPayload>(MessageEnvelope<JsonElement> envelope) =>
        envelope.Payload.Deserialize<TPayload>(SessionMessageSerializer.DefaultOptions)
            ?? throw new InvalidOperationException($"Unable to deserialize payload as {typeof(TPayload).Name}.");
}
