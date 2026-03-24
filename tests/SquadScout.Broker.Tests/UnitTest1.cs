using SquadScout.Broker.Projects;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class ScaffoldSmokeTests
{
    [Fact]
    public async Task ProjectCatalogStoresRegistrations()
    {
        var catalog = new InMemoryProjectCatalog();

        await catalog.UpsertAsync(new RegisteredProject
        {
            ProjectId = "broker",
            DisplayName = "Broker",
            RepositoryRoot = @"D:\GitHub\SquadScout"
        });

        var projects = await catalog.ListAsync();

        var project = Assert.Single(projects);
        Assert.Equal("broker", project.ProjectId);
    }

    [Fact]
    public async Task SessionOrchestratorReturnsPendingSessionDescriptors()
    {
        var orchestrator = new InMemorySessionOrchestrator(new NullRelayPublisher(), new SessionSequenceValidator());

        var session = await orchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        Assert.Equal(SessionState.Pending, session.State);
        Assert.Equal(session, await orchestrator.GetAsync(session.SessionId));
    }

    [Fact]
    public async Task BrokerMessagesEchoAppliedAcknowledgements()
    {
        var orchestrator = new InMemorySessionOrchestrator(new NullRelayPublisher(), new SessionSequenceValidator());
        var session = await orchestrator.StartAsync(new StartSessionCommand
        {
            ProjectId = "broker",
            RequestedBy = "tests"
        });

        await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            new BrokerEnvelopeCommand<object>
            {
                MessageType = SessionMessageType.Output,
                MessageId = "broker-output-1",
                CorrelationId = "corr-1",
                Payload = new { text = "hello" }
            });

        var validation = await orchestrator.ValidateClientMessageAsync(
            session.SessionId,
            new MessageEnvelope<HeartbeatPayload>
            {
                ProjectId = session.ProjectId,
                SessionId = session.SessionId,
                Generation = SessionEnvelopeContract.InitialGeneration,
                MessageType = SessionMessageType.Heartbeat,
                Direction = MessageDirection.ClientToBroker,
                ClientSequence = 1,
                AcknowledgedSequence = 1,
                MessageId = "client-heartbeat-1",
                CorrelationId = "corr-1",
                Payload = new HeartbeatPayload()
            });

        var brokerOutput = await orchestrator.RecordBrokerMessageAsync(
            session.SessionId,
            new BrokerEnvelopeCommand<object>
            {
                MessageType = SessionMessageType.Output,
                MessageId = "broker-output-2",
                CorrelationId = "corr-2",
                Payload = new { text = "world" }
            });

        Assert.Equal(SequenceValidationStatus.Accepted, validation.Status);
        Assert.Equal(1, brokerOutput.AcknowledgedSequence);
        Assert.Equal(2, brokerOutput.Sequence);
    }
}
