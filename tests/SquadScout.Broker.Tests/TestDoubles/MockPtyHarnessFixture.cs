using System.Text.Json;
using SquadScout.Broker.Pty;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests.TestDoubles;

public sealed class MockPtyHarnessFixture
{
    private long _nextMessageId = 1;
    private readonly PtySessionEnvelopePump _ptyPump;

    public MockPtyHarnessFixture(int replayBufferCapacity = SessionSequencingDefaults.ReplayBufferCapacity)
    {
        RelayPublisher = new RecordingRelayPublisher();
        Orchestrator = new InMemorySessionOrchestrator(RelayPublisher, new SessionSequenceValidator(), replayBufferCapacity);
        PtyHost = new MockPtyHost();
        _ptyPump = new PtySessionEnvelopePump(Orchestrator);
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
        await _ptyPump.PumpAvailableAsync(PtySession);
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

    public static TPayload DeserializePayload<TPayload>(MessageEnvelope<JsonElement> envelope) =>
        envelope.Payload.Deserialize<TPayload>(SessionMessageSerializer.DefaultOptions)
            ?? throw new InvalidOperationException($"Unable to deserialize payload as {typeof(TPayload).Name}.");
}
