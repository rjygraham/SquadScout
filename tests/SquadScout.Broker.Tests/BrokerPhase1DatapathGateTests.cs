using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SquadScout.Broker.Projects;
using SquadScout.Broker.Pty;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Broker.Tests.TestDoubles;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Projects;
using SquadScout.Contracts.Realtime;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class BrokerPhase1DatapathGateTests
{
    [Fact]
    public async Task InputEndpointAcceptsContractJsonAndPublishesReplayablePtyOutput()
    {
        using var factory = new BrokerPhaseGateApplicationFactory();
        using var client = factory.CreateClient();

        var session = await StartSessionAsync(client);
        Assert.Equal(SessionState.Running, session.State);

        using var response = await client.PostAsync(
            $"/api/sessions/{session.SessionId}/input",
            CreateJsonContent(CreateInputEnvelope(session, clientSequence: 1, "status --json\n")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var validation = await response.Content.ReadFromJsonAsync<SequenceValidationResult>(SessionMessageSerializer.DefaultOptions);
        Assert.NotNull(validation);
        Assert.Equal(SequenceValidationStatus.Accepted, validation!.Status);

        var ptySession = factory.PtyHost.GetRequiredSession(session.SessionId);
        Assert.Equal(["status --json\n"], ptySession.WrittenInputs);

        ptySession.EnqueueOutput("ready", afterTicks: 1);
        ptySession.EnqueueExit(0, afterTicks: 2);
        ptySession.ReleaseAll();

        var published = await factory.RelayPublisher.WaitForEnvelopeCountAsync(3);
        var sessionGroup = SessionGroupName.Create(session.ProjectId, session.SessionId);

        Assert.Equal(sessionGroup, Assert.Single(factory.RelayPublisher.JoinedSessionGroups));
        Assert.Equal(sessionGroup, Assert.Single(factory.RelayPublisher.LeftSessionGroups));

        Assert.Collection(
            published.Take(3),
            message =>
            {
                Assert.Equal(1, message.Sequence);
                Assert.Equal(SessionMessageType.SessionLifecycle, message.MessageType);
                var payload = MockPtyHarnessFixture.DeserializePayload<SessionLifecyclePayload>(message);
                Assert.Equal(SessionState.Running, payload.State);
                Assert.Equal("pty-started", payload.Reason);
            },
            message =>
            {
                Assert.Equal(2, message.Sequence);
                Assert.Equal(SessionMessageType.Output, message.MessageType);
                var payload = MockPtyHarnessFixture.DeserializePayload<OutputChunkPayload>(message);
                Assert.Equal("ready", payload.Content);
                Assert.False(payload.IsError);
            },
            message =>
            {
                Assert.Equal(3, message.Sequence);
                Assert.Equal(SessionMessageType.SessionLifecycle, message.MessageType);
                var payload = MockPtyHarnessFixture.DeserializePayload<SessionLifecyclePayload>(message);
                Assert.Equal(SessionState.Stopped, payload.State);
                Assert.Equal(0, payload.ExitCode);
                Assert.Equal("pty-exited", payload.Reason);
            });

        var replay = await factory.Orchestrator.ReplayAsync(
            session.SessionId,
            CreateReplayRequest(session, clientSequence: 2, fromSequenceInclusive: 1));

        Assert.Equal(1, replay.Payload.FromSequenceInclusive);
        Assert.Equal(3, replay.Payload.ToSequenceInclusive);
        Assert.False(replay.Payload.GapDetected);
        Assert.Collection(
            replay.Payload.Messages,
            message => Assert.Equal(1, message.Sequence),
            message => Assert.Equal(2, message.Sequence),
            message => Assert.Equal(3, message.Sequence));
    }

    [Fact]
    public async Task InputEndpointReturnsSuccessForGapDetectedClientInputAndStillWritesToPty()
    {
        using var factory = new BrokerPhaseGateApplicationFactory();
        using var client = factory.CreateClient();

        var session = await StartSessionAsync(client);

        using var firstResponse = await client.PostAsync(
            $"/api/sessions/{session.SessionId}/input",
            CreateJsonContent(CreateInputEnvelope(session, clientSequence: 1, "first\n")));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        using var gapResponse = await client.PostAsync(
            $"/api/sessions/{session.SessionId}/input",
            CreateJsonContent(CreateInputEnvelope(session, clientSequence: 3, "gap\n")));

        Assert.Equal(HttpStatusCode.OK, gapResponse.StatusCode);
        var validation = await gapResponse.Content.ReadFromJsonAsync<SequenceValidationResult>(SessionMessageSerializer.DefaultOptions);
        Assert.NotNull(validation);
        Assert.Equal(SequenceValidationStatus.GapDetected, validation!.Status);
        Assert.True(validation.IsAccepted);
        Assert.Equal(SessionEnvelopeContract.InitialGeneration, validation.Generation);
        Assert.Equal(3, validation.ClientSequence);
        Assert.Equal(1, validation.LastAcceptedClientSequence);
        Assert.Equal(2, validation.ExpectedClientSequence);
        Assert.Null(validation.AppliedAcknowledgedSequence);
        Assert.Contains("gap", validation.Reason, StringComparison.OrdinalIgnoreCase);

        var ptySession = factory.PtyHost.GetRequiredSession(session.SessionId);
        Assert.Equal(["first\n", "gap\n"], ptySession.WrittenInputs);
    }

    [Fact]
    public async Task TelemetryEndpointExportsSecretSafeSessionDiagnostics()
    {
        using var factory = new BrokerPhaseGateApplicationFactory();
        using var client = factory.CreateClient();

        var session = await StartSessionAsync(client);

        using var inputResponse = await client.PostAsync(
            $"/api/sessions/{session.SessionId}/input",
            CreateJsonContent(CreateInputEnvelope(session, clientSequence: 1, "password=swordfish\n")));
        Assert.Equal(HttpStatusCode.OK, inputResponse.StatusCode);

        var ptySession = factory.PtyHost.GetRequiredSession(session.SessionId);
        ptySession.EnqueueOutput("{\"token\":\"abc\",\"Authorization\":\"Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.signature\"}", afterTicks: 1);
        ptySession.ReleaseNext();
        _ = await factory.RelayPublisher.WaitForEnvelopeCountAsync(2);

        using var response = await client.GetAsync($"/api/sessions/{session.SessionId}/telemetry");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var exportJson = await response.Content.ReadAsStringAsync();
        var telemetry = JsonSerializer.Deserialize<SessionTelemetrySnapshot>(exportJson, SessionMessageSerializer.DefaultOptions);
        Assert.NotNull(telemetry);
        Assert.NotEmpty(telemetry!.RecentEnvelopes);
        Assert.NotEmpty(telemetry.RecentEvents);
        Assert.DoesNotContain("swordfish", exportJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"abc\"", exportJson, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.signature", exportJson, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", exportJson, StringComparison.Ordinal);
        Assert.Contains(
            telemetry.RecentEnvelopes,
            envelope => envelope.MessageType == SessionMessageType.Input
                        && envelope.PayloadPreview.Contains("[REDACTED]", StringComparison.Ordinal));
    }

    private static async Task<SessionDescriptor> StartSessionAsync(HttpClient client)
    {
        using var response = await client.PostAsync(
            "/api/sessions",
            CreateJsonContent(new StartSessionCommand
            {
                ProjectId = "broker",
                RequestedBy = "tests",
                Arguments = ["--project", "broker"]
            }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var session = await response.Content.ReadFromJsonAsync<SessionDescriptor>(SessionMessageSerializer.DefaultOptions);
        return session ?? throw new InvalidOperationException("Session start response did not contain a descriptor.");
    }

    private static MessageEnvelope<InputChunkPayload> CreateInputEnvelope(
        SessionDescriptor session,
        long clientSequence,
        string content) =>
        new()
        {
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.Input,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = clientSequence,
            MessageId = $"client-input-{clientSequence}",
            CorrelationId = "corr-input",
            Payload = new InputChunkPayload
            {
                Content = content
            }
        };

    private static MessageEnvelope<ReplayRequestPayload> CreateReplayRequest(
        SessionDescriptor session,
        long clientSequence,
        long fromSequenceInclusive) =>
        new()
        {
            ProjectId = session.ProjectId,
            SessionId = session.SessionId,
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.ReplayRequest,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = clientSequence,
            MessageId = $"client-replay-{clientSequence}",
            CorrelationId = "corr-replay",
            Payload = new ReplayRequestPayload
            {
                FromSequenceInclusive = fromSequenceInclusive,
                MaximumMessages = 10,
                Reason = ReplayRequestReason.ReconnectResume
            }
        };

    private static StringContent CreateJsonContent<T>(T payload) =>
        new(
            JsonSerializer.Serialize(payload, SessionMessageSerializer.DefaultOptions),
            Encoding.UTF8,
            "application/json");

    private sealed class BrokerPhaseGateApplicationFactory : WebApplicationFactory<Program>
    {
        private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));

        private readonly InMemoryProjectCatalog _projectCatalog = new();

        public BrokerPhaseGateApplicationFactory()
        {
            _projectCatalog.UpsertAsync(new RegisteredProject
            {
                ProjectId = "broker",
                DisplayName = "Broker",
                RepositoryRoot = RepositoryRoot
            }).GetAwaiter().GetResult();
        }

        public MockPtyHost PtyHost { get; } = new();

        public RecordingRelayPublisher RelayPublisher { get; } = new();

        public ISessionOrchestrator Orchestrator => Services.GetRequiredService<ISessionOrchestrator>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IProjectCatalog>();
                services.RemoveAll<IPtyHost>();
                services.RemoveAll<IRelayPublisher>();

                services.AddSingleton<IProjectCatalog>(_projectCatalog);
                services.AddSingleton<IPtyHost>(PtyHost);
                services.AddSingleton<IRelayPublisher>(RelayPublisher);
            });
        }
    }
}
