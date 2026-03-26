using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SquadScout.Broker.Projects;
using SquadScout.Broker.Realtime;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Realtime;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class BrokerControlMessageHandlerTests
{
    [Fact]
    public async Task HandleAsyncPublishesProjectCatalogResponse()
    {
        var publisher = new RecordingRelayPublisher();
        var handler = CreateHandler(
            publisher,
            projects:
            [
                new RegisteredProject
                {
                    ProjectId = "proj-01",
                    DisplayName = "Project One",
                    RepositoryRoot = @"D:\GitHub\ProjectOne"
                }
            ]);

        await handler.HandleAsync(
            JsonSerializer.SerializeToElement(
                CreateRequestEnvelope(
                    SessionMessageType.ProjectCatalogRequest,
                    new ProjectCatalogRequestPayload
                    {
                        RequestedBy = "tests"
                    }),
                SessionMessageSerializer.DefaultOptions),
            CancellationToken.None);

        var response = Assert.Single(publisher.Envelopes);
        Assert.Equal(SessionMessageType.ProjectCatalogResponse, response.MessageType);
        Assert.Equal(MessageDirection.BrokerToClient, response.Direction);
        Assert.Equal("request-1", response.CausationId);

        var payload = response.Payload.Deserialize<ProjectCatalogResponsePayload>(SessionMessageSerializer.DefaultOptions);
        Assert.NotNull(payload);
        Assert.Single(payload!.Projects);
        Assert.Equal("proj-01", payload.Projects[0].ProjectId);
    }

    [Fact]
    public async Task HandleAsyncPublishesStartSessionResponse()
    {
        var publisher = new RecordingRelayPublisher();
        var expectedSession = new SessionDescriptor
        {
            SessionId = "session-123",
            ProjectId = "proj-01",
            State = SessionState.Running,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var handler = CreateHandler(
            publisher,
            startResult: expectedSession);

        await handler.HandleAsync(
            JsonSerializer.SerializeToElement(
                CreateRequestEnvelope(
                    SessionMessageType.StartSessionRequest,
                    new StartSessionRequestPayload
                    {
                        Command = new StartSessionCommand
                        {
                            ProjectId = "proj-01",
                            RequestedBy = "tests"
                        }
                    }),
                SessionMessageSerializer.DefaultOptions),
            CancellationToken.None);

        var response = Assert.Single(publisher.Envelopes);
        Assert.Equal(SessionMessageType.StartSessionResponse, response.MessageType);
        var payload = response.Payload.Deserialize<StartSessionResponsePayload>(SessionMessageSerializer.DefaultOptions);
        Assert.NotNull(payload);
        Assert.Equal(expectedSession.SessionId, payload!.Session?.SessionId);
    }

    [Fact]
    public async Task HandleAsyncPublishesSessionStatusResponse()
    {
        var publisher = new RecordingRelayPublisher();
        var expectedSession = new SessionDescriptor
        {
            SessionId = "session-xyz",
            ProjectId = "proj-01",
            State = SessionState.Running,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var handler = CreateHandler(
            publisher,
            currentSession: expectedSession);

        await handler.HandleAsync(
            JsonSerializer.SerializeToElement(
                CreateRequestEnvelope(
                    SessionMessageType.SessionStatusRequest,
                    new SessionStatusRequestPayload
                    {
                        SessionId = expectedSession.SessionId
                    }),
                SessionMessageSerializer.DefaultOptions),
            CancellationToken.None);

        var response = Assert.Single(publisher.Envelopes);
        Assert.Equal(SessionMessageType.SessionStatusResponse, response.MessageType);
        var payload = response.Payload.Deserialize<SessionStatusResponsePayload>(SessionMessageSerializer.DefaultOptions);
        Assert.NotNull(payload);
        Assert.Equal(expectedSession.SessionId, payload!.Session?.SessionId);
    }

    private static BrokerControlMessageHandler CreateHandler(
        RecordingRelayPublisher publisher,
        IReadOnlyCollection<RegisteredProject>? projects = null,
        SessionDescriptor? startResult = null,
        SessionDescriptor? currentSession = null) =>
        new(
            new StubProjectCatalog(projects ?? Array.Empty<RegisteredProject>()),
            new StubSessionRelay(startResult),
            new StubSessionOrchestrator(currentSession),
            publisher,
            NullLogger<BrokerControlMessageHandler>.Instance);

    private static MessageEnvelope<TPayload> CreateRequestEnvelope<TPayload>(
        SessionMessageType messageType,
        TPayload payload) =>
        new()
        {
            ProjectId = BrokerControlChannel.ProjectId,
            SessionId = BrokerControlChannel.SessionId,
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = messageType,
            Direction = MessageDirection.ClientToBroker,
            TimestampUtc = DateTimeOffset.UtcNow,
            MessageId = "request-1",
            CorrelationId = "correlation-1",
            Payload = payload
        };

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

    private sealed class StubProjectCatalog(IReadOnlyCollection<RegisteredProject> projects) : IProjectCatalog
    {
        public Task<RegisteredProject?> GetAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(projects.FirstOrDefault(project => string.Equals(project.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyCollection<RegisteredProject>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(projects);

        public Task UpsertAsync(RegisteredProject project, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubSessionRelay(SessionDescriptor? startResult) : ISessionRelay
    {
        public Task<SessionDescriptor> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(startResult ?? new SessionDescriptor
            {
                SessionId = "session-default",
                ProjectId = command.ProjectId,
                State = SessionState.Running,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });

        public Task<SessionDescriptor> StopAsync(string sessionId, StopSessionCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SequenceValidationResult> RelayInputAsync(
            string sessionId,
            MessageEnvelope<InputChunkPayload> envelope,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubSessionOrchestrator(SessionDescriptor? session) : ISessionOrchestrator
    {
        public Task<SessionDescriptor?> GetAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(session);

        public Task<SessionDescriptor> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MessageEnvelope<TPayload>> RecordBrokerMessageAsync<TPayload>(string sessionId, BrokerEnvelopeCommand<TPayload> command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SequenceValidationResult> ValidateClientMessageAsync<TPayload>(string sessionId, MessageEnvelope<TPayload> envelope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SequenceValidationResult> AcceptClientMessageAsync<TPayload>(string sessionId, MessageEnvelope<TPayload> envelope, Func<MessageEnvelope<TPayload>, CancellationToken, Task> onAcceptedAsync, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MessageEnvelope<ReplayResponsePayload>> ReplayAsync(string sessionId, MessageEnvelope<ReplayRequestPayload> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionTelemetrySnapshot> ExportTelemetryAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> ResetGenerationAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
