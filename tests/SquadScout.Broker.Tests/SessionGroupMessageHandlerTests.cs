using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SquadScout.Broker.Realtime;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class SessionGroupMessageHandlerTests
{
    [Fact]
    public async Task HandleAsyncRoutesInputEnvelopeThroughSessionRelay()
    {
        var relay = new RecordingSessionRelay();
        var services = CreateServices(relay, new StubSessionOrchestrator(), new RecordingRelayPublisher());
        var handler = new SessionGroupMessageHandler(
            services,
            new SessionGroupResolver(),
            NullLogger<SessionGroupMessageHandler>.Instance);

        var envelope = new MessageEnvelope<InputChunkPayload>
        {
            ProjectId = "proj-01",
            SessionId = "session-abc",
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.Input,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = 1,
            MessageId = "client-input-1",
            CorrelationId = "corr-input",
            Payload = new InputChunkPayload
            {
                Content = "status\n"
            }
        };

        await handler.HandleAsync(
            "session:proj-01:session-abc",
            JsonSerializer.SerializeToElement(envelope, SessionMessageSerializer.DefaultOptions),
            CancellationToken.None);

        var forwarded = Assert.Single(relay.Inputs);
        Assert.Equal(envelope.SessionId, forwarded.SessionId);
        Assert.Equal("status\n", forwarded.Payload.Content);
    }

    [Fact]
    public async Task HandleAsyncPublishesReplayResponsesBackToTheSessionGroup()
    {
        var relay = new RecordingSessionRelay();
        var orchestrator = new StubSessionOrchestrator();
        var publisher = new RecordingRelayPublisher();
        var services = CreateServices(relay, orchestrator, publisher);
        var handler = new SessionGroupMessageHandler(
            services,
            new SessionGroupResolver(),
            NullLogger<SessionGroupMessageHandler>.Instance);

        var request = new MessageEnvelope<ReplayRequestPayload>
        {
            ProjectId = "proj-01",
            SessionId = "session-abc",
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.ReplayRequest,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = 2,
            MessageId = "client-replay-2",
            CorrelationId = "corr-replay",
            Payload = new ReplayRequestPayload
            {
                FromSequenceInclusive = 3,
                Reason = ReplayRequestReason.ReconnectResume
            }
        };

        orchestrator.ReplayResponse = new MessageEnvelope<ReplayResponsePayload>
        {
            ProjectId = request.ProjectId,
            SessionId = request.SessionId,
            Generation = request.Generation,
            MessageType = SessionMessageType.ReplayResponse,
            Direction = MessageDirection.BrokerToClient,
            MessageId = "broker-replay-1",
            CorrelationId = request.CorrelationId,
            CausationId = request.MessageId,
            Payload = new ReplayResponsePayload
            {
                Generation = request.Generation,
                FromSequenceInclusive = 3,
                ToSequenceInclusive = 4
            }
        };

        await handler.HandleAsync(
            "session:proj-01:session-abc",
            JsonSerializer.SerializeToElement(request, SessionMessageSerializer.DefaultOptions),
            CancellationToken.None);

        Assert.NotNull(orchestrator.LastReplayRequest);
        Assert.Equal(request.MessageId, orchestrator.LastReplayRequest!.MessageId);
        var published = Assert.Single(publisher.Envelopes);
        Assert.Equal(SessionMessageType.ReplayResponse, published.MessageType);
        Assert.Equal("client-replay-2", published.CausationId);
    }

    private static IServiceProvider CreateServices(
        RecordingSessionRelay relay,
        StubSessionOrchestrator orchestrator,
        RecordingRelayPublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISessionRelay>(relay);
        services.AddSingleton<ISessionOrchestrator>(orchestrator);
        services.AddSingleton<IRelayPublisher>(publisher);
        return services.BuildServiceProvider();
    }

    private sealed class RecordingSessionRelay : ISessionRelay
    {
        public List<MessageEnvelope<InputChunkPayload>> Inputs { get; } = [];

        public Task<SessionDescriptor> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionDescriptor> StopAsync(string sessionId, StopSessionCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SequenceValidationResult> RelayInputAsync(
            string sessionId,
            MessageEnvelope<InputChunkPayload> envelope,
            CancellationToken cancellationToken = default)
        {
            Inputs.Add(envelope);
            return Task.FromResult(new SequenceValidationResult
            {
                Status = SequenceValidationStatus.Accepted,
                Generation = envelope.Generation,
                ClientSequence = envelope.ClientSequence
            });
        }
    }

    private sealed class StubSessionOrchestrator : ISessionOrchestrator
    {
        public MessageEnvelope<ReplayRequestPayload>? LastReplayRequest { get; private set; }

        public MessageEnvelope<ReplayResponsePayload> ReplayResponse { get; set; } = new()
        {
            ProjectId = "proj-01",
            SessionId = "session-abc",
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.ReplayResponse,
            Direction = MessageDirection.BrokerToClient,
            MessageId = "broker-replay-default",
            CorrelationId = "corr-replay",
            Payload = new ReplayResponsePayload
            {
                Generation = SessionEnvelopeContract.InitialGeneration
            }
        };

        public Task<SessionDescriptor?> GetAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionDescriptor?>(null);

        public Task<SessionDescriptor> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MessageEnvelope<TPayload>> RecordBrokerMessageAsync<TPayload>(string sessionId, BrokerEnvelopeCommand<TPayload> command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SequenceValidationResult> ValidateClientMessageAsync<TPayload>(string sessionId, MessageEnvelope<TPayload> envelope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SequenceValidationResult> AcceptClientMessageAsync<TPayload>(string sessionId, MessageEnvelope<TPayload> envelope, Func<MessageEnvelope<TPayload>, CancellationToken, Task> onAcceptedAsync, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MessageEnvelope<ReplayResponsePayload>> ReplayAsync(string sessionId, MessageEnvelope<ReplayRequestPayload> request, CancellationToken cancellationToken = default)
        {
            LastReplayRequest = request;
            return Task.FromResult(ReplayResponse);
        }

        public Task<SessionTelemetrySnapshot> ExportTelemetryAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> ResetGenerationAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRelayPublisher : IRelayPublisher
    {
        public List<MessageEnvelope<JsonElement>> Envelopes { get; } = [];

        public Task PublishSessionStartedAsync(SessionDescriptor session, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PublishEnvelopeAsync<TPayload>(MessageEnvelope<TPayload> envelope, CancellationToken cancellationToken = default)
        {
            Envelopes.Add(new MessageEnvelope<JsonElement>
            {
                ContractVersion = envelope.ContractVersion,
                ProjectId = envelope.ProjectId,
                SessionId = envelope.SessionId,
                Generation = envelope.Generation,
                MessageType = envelope.MessageType,
                Direction = envelope.Direction,
                Sequence = envelope.Sequence,
                ClientSequence = envelope.ClientSequence,
                AcknowledgedSequence = envelope.AcknowledgedSequence,
                TimestampUtc = envelope.TimestampUtc,
                MessageId = envelope.MessageId,
                CorrelationId = envelope.CorrelationId,
                CausationId = envelope.CausationId,
                Payload = JsonSerializer.SerializeToElement(envelope.Payload, SessionMessageSerializer.DefaultOptions)
            });

            return Task.CompletedTask;
        }
    }
}
