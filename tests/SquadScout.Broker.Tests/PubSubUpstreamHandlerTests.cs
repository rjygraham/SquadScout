using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SquadScout.Broker.Configuration;
using SquadScout.Broker.Realtime;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Broker.Tests.TestDoubles;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Realtime;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class PubSubUpstreamHandlerTests
{
    private const string WebsiteInstanceIdEnvironmentVariable = "WEBSITE_INSTANCE_ID";
    private static readonly object EnvironmentVariableSync = new();
    private const string WebPubSubOrigin = "squadscout.webpubsub.azure.com";
    private const string TrustedUpstreamPrincipalId = "webpubsub-mi-01";
    private const string UpstreamAccessKey = "test-access-key";

    [Fact]
    public async Task HandleAsyncForSessionInputEventRelaysEnvelopeToSessionRelay()
    {
        var relay = new RecordingSessionRelay();
        var handler = CreateHandler(relay: relay);
        using var body = CreateJsonBody(CreateInputEnvelope());
        var headers = CreateSignedHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}");

        var response = await handler.HandleAsync("POST", headers, body, CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(WebPubSubOrigin, response.AllowedOrigin);
        Assert.Null(response.Body);
        Assert.NotNull(relay.LastInputEnvelope);
        Assert.Equal("session-abc", relay.LastInputEnvelope!.SessionId);
        Assert.Equal("broker", relay.LastInputEnvelope.ProjectId);
        Assert.Equal("status --json\n", relay.LastInputEnvelope.Payload!.Content);
    }

    [Fact]
    public async Task HandleAsyncForReplayRequestEventPublishesReplayResponseToSessionGroup()
    {
        var publisher = new RecordingRelayPublisher();
        var orchestrator = new RecordingSessionOrchestrator();
        var handler = CreateHandler(orchestrator: orchestrator, publisher: publisher);
        var request = CreateReplayRequestEnvelope();
        orchestrator.ReplayResponse = CreateReplayResponseEnvelope(request);
        using var body = CreateJsonBody(request);
        var headers = CreateSignedHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.ReplayRequest}");

        var response = await handler.HandleAsync("POST", headers, body, CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(orchestrator.LastReplayRequest);
        Assert.Equal(request.MessageId, orchestrator.LastReplayRequest!.MessageId);

        var published = Assert.Single(publisher.PublishedEnvelopes);
        Assert.Equal(SessionMessageType.ReplayResponse, published.MessageType);
        Assert.Equal(request.CorrelationId, published.CorrelationId);
        Assert.Equal(request.MessageId, published.CausationId);
    }

    [Fact]
    public async Task HandleAsyncRejectsMalformedInputEnvelopeWithoutCallingRelay()
    {
        var relay = new RecordingSessionRelay();
        var handler = CreateHandler(relay: relay);
        using var body = CreateJsonBody(new
        {
            projectId = "broker",
            sessionId = "session-abc",
            generation = 1,
            messageType = "output",
            direction = "clientToBroker",
            payload = new
            {
                content = "status\n"
            }
        });

        var response = await handler.HandleAsync(
            "POST",
            CreateSignedHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}"),
            body,
            CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("input or replay-request envelope", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Null(relay.LastInputEnvelope);
    }

    [Fact]
    public async Task HandleAsyncRejectsEnvelopeThatCannotMapToPhaseOneSessionGroup()
    {
        var relay = new RecordingSessionRelay();
        var handler = CreateHandler(relay: relay);
        var envelope = CreateInputEnvelope() with
        {
            ProjectId = "broker:west"
        };
        using var body = CreateJsonBody(envelope);

        var response = await handler.HandleAsync(
            "POST",
            CreateSignedHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}"),
            body,
            CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Phase 1 session group", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("projectId", response.Body, StringComparison.Ordinal);
        Assert.Null(relay.LastInputEnvelope);
    }

    [Fact]
    public async Task HandleAsyncReturnsWebhookValidationHeadersForOptionsRequests()
    {
        var handler = CreateHandler();
        using var body = CreateJsonBody(new { });
        var headers = CreateHeaders();
        headers.Add(WebPubSubUpstreamHandler.WebHookRequestOriginHeaderName, WebPubSubOrigin);

        var response = await handler.HandleAsync("OPTIONS", headers, body, CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WebPubSubOrigin, response.AllowedOrigin);
    }

    [Fact]
    public async Task HandleAsyncSurfacesValidationConflictsBackToWebPubSub()
    {
        var relay = new RecordingSessionRelay
        {
            ValidationResult = new SequenceValidationResult
            {
                Status = SequenceValidationStatus.StaleGeneration,
                Generation = 2,
                ClientSequence = 4,
                Reason = "generation mismatch"
            }
        };

        var handler = CreateHandler(relay: relay);
        using var body = CreateJsonBody(CreateInputEnvelope());

        var response = await handler.HandleAsync(
            "POST",
            CreateSignedHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}"),
            body,
            CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        Assert.Contains("generation mismatch", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsyncRejectsMissingSignatureWithoutCallingRelay()
    {
        var relay = new RecordingSessionRelay();
        var handler = CreateHandler(relay: relay);
        using var body = CreateJsonBody(CreateInputEnvelope());
        var headers = CreateHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}");
        headers.Add(WebPubSubUpstreamHandler.WebHookRequestOriginHeaderName, WebPubSubOrigin);
        headers.Add(WebPubSubUpstreamHandler.CloudEventConnectionIdHeaderName, "conn-123");

        var response = await handler.HandleAsync("POST", headers, body, CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("signature", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Null(relay.LastInputEnvelope);
    }

    [Fact]
    public async Task HandleAsyncRejectsInvalidSignatureWithoutCallingRelay()
    {
        var relay = new RecordingSessionRelay();
        var handler = CreateHandler(relay: relay);
        using var body = CreateJsonBody(CreateInputEnvelope());
        var headers = CreateHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}");
        headers.Add(WebPubSubUpstreamHandler.WebHookRequestOriginHeaderName, WebPubSubOrigin);
        headers.Add(WebPubSubUpstreamHandler.CloudEventConnectionIdHeaderName, "conn-123");
        headers.Add(WebPubSubUpstreamAuthenticator.CloudEventSignatureHeaderName, "sha256=deadbeef");

        var response = await handler.HandleAsync("POST", headers, body, CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("signature", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Null(relay.LastInputEnvelope);
    }

    [Fact]
    public async Task HandleAsyncAcceptsWebHookSignatureAlias()
    {
        var relay = new RecordingSessionRelay();
        var handler = CreateHandler(relay: relay);
        using var body = CreateJsonBody(CreateInputEnvelope());
        var headers = CreateHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}");
        headers.Add(WebPubSubUpstreamHandler.WebHookRequestOriginHeaderName, WebPubSubOrigin);
        headers.Add(WebPubSubUpstreamHandler.CloudEventConnectionIdHeaderName, "conn-123");
        headers.Add(
            WebPubSubUpstreamAuthenticator.WebHookSignatureHeaderName,
            ComputeSignature(UpstreamAccessKey, "conn-123"));

        var response = await handler.HandleAsync("POST", headers, body, CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(relay.LastInputEnvelope);
    }

    [Fact]
    public async Task HandleAsyncAcceptsTrustedManagedIdentityRequestWithoutSignature()
    {
        var relay = new RecordingSessionRelay();
        var handler = CreateHandler(
            relay: relay,
            options: new AzureWebPubSubOptions
            {
                ConnectionString = CreateConnectionString(),
                TrustedUpstreamPrincipalIds = [TrustedUpstreamPrincipalId]
            });

        using var body = CreateJsonBody(CreateInputEnvelope());
        var headers = CreateHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}");
        headers.Add(WebPubSubUpstreamHandler.WebHookRequestOriginHeaderName, WebPubSubOrigin);
        headers.Add("x-ms-client-principal-id", TrustedUpstreamPrincipalId);

        var response = await WithEnvironmentVariable(
            WebsiteInstanceIdEnvironmentVariable,
            "trusted-boundary",
            () => handler.HandleAsync("POST", headers, body, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(relay.LastInputEnvelope);
    }

    [Fact]
    public async Task HandleAsyncRejectsUntrustedManagedIdentityRequest()
    {
        var relay = new RecordingSessionRelay();
        var handler = CreateHandler(
            relay: relay,
            options: new AzureWebPubSubOptions
            {
                ConnectionString = CreateConnectionString(),
                TrustedUpstreamPrincipalIds = [TrustedUpstreamPrincipalId]
            });

        using var body = CreateJsonBody(CreateInputEnvelope());
        var headers = CreateHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}");
        headers.Add(WebPubSubUpstreamHandler.WebHookRequestOriginHeaderName, WebPubSubOrigin);
        headers.Add("x-ms-client-principal-id", "unexpected-principal");

        var response = await WithEnvironmentVariable(
            WebsiteInstanceIdEnvironmentVariable,
            "trusted-boundary",
            () => handler.HandleAsync("POST", headers, body, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("principal", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Null(relay.LastInputEnvelope);
    }

    private static WebPubSubUpstreamHandler CreateHandler(
        RecordingSessionRelay? relay = null,
        RecordingSessionOrchestrator? orchestrator = null,
        RecordingRelayPublisher? publisher = null,
        AzureWebPubSubOptions? options = null) =>
        new(
            new WebPubSubUpstreamAuthenticator(
                Options.Create(options ?? CreateOptionsWithAccessKey()),
                NullLogger<WebPubSubUpstreamAuthenticator>.Instance),
            relay ?? new RecordingSessionRelay(),
            orchestrator ?? new RecordingSessionOrchestrator(),
            publisher ?? new RecordingRelayPublisher(),
            NullLogger<WebPubSubUpstreamHandler>.Instance);

    private static HeaderDictionary CreateHeaders(string? cloudEventType = null)
    {
        var headers = new HeaderDictionary();
        if (!string.IsNullOrWhiteSpace(cloudEventType))
        {
            headers.Add(WebPubSubUpstreamHandler.CloudEventTypeHeaderName, cloudEventType);
        }

        return headers;
    }

    private static HeaderDictionary CreateSignedHeaders(
        string cloudEventType,
        string connectionId = "conn-123")
    {
        var headers = CreateHeaders(cloudEventType);
        headers.Add(WebPubSubUpstreamHandler.WebHookRequestOriginHeaderName, WebPubSubOrigin);
        headers.Add(WebPubSubUpstreamHandler.CloudEventConnectionIdHeaderName, connectionId);
        headers.Add(
            WebPubSubUpstreamAuthenticator.CloudEventSignatureHeaderName,
            ComputeSignature(UpstreamAccessKey, connectionId));
        return headers;
    }

    private static MemoryStream CreateJsonBody<TPayload>(TPayload payload) =>
        new(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, SessionMessageSerializer.DefaultOptions)));

    private static MessageEnvelope<InputChunkPayload> CreateInputEnvelope() =>
        new()
        {
            ProjectId = "broker",
            SessionId = "session-abc",
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.Input,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = 1,
            MessageId = "client-input-1",
            CorrelationId = "corr-input",
            Payload = new InputChunkPayload
            {
                Content = "status --json\n"
            }
        };

    private static MessageEnvelope<ReplayRequestPayload> CreateReplayRequestEnvelope() =>
        new()
        {
            ProjectId = "broker",
            SessionId = "session-abc",
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.ReplayRequest,
            Direction = MessageDirection.ClientToBroker,
            ClientSequence = 2,
            AcknowledgedSequence = 1,
            MessageId = "client-replay-1",
            CorrelationId = "corr-replay",
            Payload = new ReplayRequestPayload
            {
                FromSequenceInclusive = 2,
                Reason = ReplayRequestReason.GapDetected
            }
        };

    private static MessageEnvelope<ReplayResponsePayload> CreateReplayResponseEnvelope(MessageEnvelope<ReplayRequestPayload> request) =>
        new()
        {
            ProjectId = request.ProjectId,
            SessionId = request.SessionId,
            Generation = request.Generation,
            MessageType = SessionMessageType.ReplayResponse,
            Direction = MessageDirection.BrokerToClient,
            AcknowledgedSequence = request.AcknowledgedSequence,
            MessageId = "broker-replay-1",
            CorrelationId = request.CorrelationId,
            CausationId = request.MessageId,
            Payload = new ReplayResponsePayload
            {
                Generation = request.Generation,
                FromSequenceInclusive = 2,
                ToSequenceInclusive = 2,
                AvailableFromSequence = 1,
                AvailableToSequence = 2,
                GapDetected = false,
                HasMore = false,
                IsComplete = true,
                Messages =
                [
                    new MessageEnvelope<JsonElement>
                    {
                        ProjectId = request.ProjectId,
                        SessionId = request.SessionId,
                        Generation = request.Generation,
                        MessageType = SessionMessageType.Output,
                        Direction = MessageDirection.BrokerToClient,
                        Sequence = 2,
                        MessageId = "output-2",
                        Payload = JsonSerializer.SerializeToElement(
                            new OutputChunkPayload
                            {
                                Content = "resumed-output",
                                IsError = false
                            },
                            SessionMessageSerializer.DefaultOptions)
                    }
                ]
            }
        };

    private static AzureWebPubSubOptions CreateOptionsWithAccessKey() =>
        new()
        {
            ConnectionString = CreateConnectionString()
        };

    private static string CreateConnectionString() =>
        $"Endpoint=https://{WebPubSubOrigin};AccessKey={UpstreamAccessKey};Version=1.0;";

    private static string ComputeSignature(string accessKey, string connectionId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(accessKey));
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(connectionId));
        return $"sha256={Convert.ToHexString(hashBytes)}";
    }

    private static Task<T> WithEnvironmentVariable<T>(string name, string? value, Func<Task<T>> action)
    {
        lock (EnvironmentVariableSync)
        {
            var original = Environment.GetEnvironmentVariable(name);
            try
            {
                Environment.SetEnvironmentVariable(name, value);
                return Task.FromResult(action().GetAwaiter().GetResult());
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, original);
            }
        }
    }

    private sealed class RecordingSessionRelay : ISessionRelay
    {
        public SequenceValidationResult ValidationResult { get; set; } = new()
        {
            Status = SequenceValidationStatus.Accepted,
            Generation = SessionEnvelopeContract.InitialGeneration,
            ClientSequence = 1
        };

        public MessageEnvelope<InputChunkPayload>? LastInputEnvelope { get; private set; }

        public Task<SessionDescriptor> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionDescriptor> StopAsync(string sessionId, StopSessionCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SequenceValidationResult> RelayInputAsync(
            string sessionId,
            MessageEnvelope<InputChunkPayload> envelope,
            CancellationToken cancellationToken = default)
        {
            LastInputEnvelope = envelope;
            return Task.FromResult(ValidationResult);
        }
    }

    private sealed class RecordingSessionOrchestrator : ISessionOrchestrator
    {
        public MessageEnvelope<ReplayRequestPayload>? LastReplayRequest { get; private set; }

        public MessageEnvelope<ReplayResponsePayload> ReplayResponse { get; set; } = new()
        {
            ProjectId = "broker",
            SessionId = "session-abc",
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.ReplayResponse,
            Direction = MessageDirection.BrokerToClient,
            MessageId = "replay-response",
            Payload = new ReplayResponsePayload
            {
                Generation = SessionEnvelopeContract.InitialGeneration,
                Messages = []
            }
        };

        public Task<SessionDescriptor?> GetAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionDescriptor> StartAsync(StartSessionCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MessageEnvelope<TPayload>> RecordBrokerMessageAsync<TPayload>(
            string sessionId,
            BrokerEnvelopeCommand<TPayload> command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SequenceValidationResult> ValidateClientMessageAsync<TPayload>(
            string sessionId,
            MessageEnvelope<TPayload> envelope,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SequenceValidationResult> AcceptClientMessageAsync<TPayload>(
            string sessionId,
            MessageEnvelope<TPayload> envelope,
            Func<MessageEnvelope<TPayload>, CancellationToken, Task> onAcceptedAsync,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MessageEnvelope<ReplayResponsePayload>> ReplayAsync(
            string sessionId,
            MessageEnvelope<ReplayRequestPayload> request,
            CancellationToken cancellationToken = default)
        {
            LastReplayRequest = request;
            return Task.FromResult(ReplayResponse);
        }

        public Task<SessionTelemetrySnapshot> ExportTelemetryAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> ResetGenerationAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
