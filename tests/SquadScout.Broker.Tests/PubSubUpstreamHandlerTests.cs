using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SquadScout.Functions.Configuration;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Realtime;
using SquadScout.Functions.Upstream;

namespace SquadScout.Broker.Tests;

public sealed class PubSubUpstreamHandlerTests
{
    private const string WebsiteInstanceIdEnvironmentVariable = "WEBSITE_INSTANCE_ID";
    private static readonly object EnvironmentVariableSync = new();
    private const string WebPubSubOrigin = "squadscout.webpubsub.azure.com";
    private const string TrustedUpstreamPrincipalId = "webpubsub-mi-01";
    private const string UpstreamAccessKey = "test-access-key";

    [Fact]
    public async Task HandleAsyncForSessionInputEventForwardsEnvelopeToBrokerInputEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler((request, cancellationToken) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        new
                        {
                            status = "accepted"
                        },
                        SessionMessageSerializer.DefaultOptions),
                    Encoding.UTF8,
                    "application/json")
            });
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:5071")
        };

        var handler = CreateHandler(httpClient, CreateOptionsWithAccessKey());
        var envelope = CreateInputEnvelope();
        using var body = CreateJsonBody(envelope);
        var headers = CreateSignedHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}");

        var response = await handler.HandleAsync("POST", headers, body, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(WebPubSubOrigin, response.AllowedOrigin);
        Assert.Null(response.Body);
        Assert.NotNull(capturedRequest);
        Assert.Equal("http://127.0.0.1:5071/api/sessions/session-abc/input", capturedRequest!.RequestUri!.ToString());

        var forwardedJson = await capturedRequest.Content!.ReadAsStringAsync();
        var forwardedEnvelope = JsonSerializer.Deserialize<MessageEnvelope<InputChunkPayload>>(
            forwardedJson,
            SessionMessageSerializer.DefaultOptions);

        Assert.NotNull(forwardedEnvelope);
        Assert.Equal(envelope.SessionId, forwardedEnvelope!.SessionId);
        Assert.Equal(envelope.ProjectId, forwardedEnvelope.ProjectId);
        Assert.Equal(envelope.Payload.Content, forwardedEnvelope.Payload!.Content);
    }

    [Fact]
    public async Task HandleAsyncRejectsMalformedInputEnvelopeWithoutCallingBroker()
    {
        var brokerCalled = false;
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler((request, cancellationToken) =>
        {
            brokerCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:5071")
        };

        var handler = CreateHandler(httpClient, CreateOptionsWithAccessKey());
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("input envelope", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.False(brokerCalled);
    }

    [Fact]
    public async Task HandleAsyncRejectsEnvelopeThatCannotMapToPhaseOneSessionGroup()
    {
        var brokerCalled = false;
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler((request, cancellationToken) =>
        {
            brokerCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:5071")
        };

        var handler = CreateHandler(httpClient, CreateOptionsWithAccessKey());
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Phase 1 session group", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("projectId", response.Body, StringComparison.Ordinal);
        Assert.False(brokerCalled);
    }

    [Fact]
    public async Task HandleAsyncReturnsWebhookValidationHeadersForOptionsRequests()
    {
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler((request, cancellationToken) =>
        {
            throw new InvalidOperationException("The broker should not be called for webhook validation.");
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:5071")
        };

        var handler = CreateHandler(httpClient, CreateOptionsWithAccessKey());
        using var body = CreateJsonBody(new { });
        var headers = CreateHeaders();
        headers.Add(WebPubSubUpstreamHandler.WebHookRequestOriginHeaderName, WebPubSubOrigin);

        var response = await handler.HandleAsync("OPTIONS", headers, body, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WebPubSubOrigin, response.AllowedOrigin);
    }

    [Fact]
    public async Task HandleAsyncSurfacesBrokerConflictsBackToWebPubSub()
    {
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler((request, cancellationToken) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        new
                        {
                            status = "gapDetected"
                        },
                        SessionMessageSerializer.DefaultOptions),
                    Encoding.UTF8,
                    "application/json")
            });
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:5071")
        };

        var handler = CreateHandler(httpClient, CreateOptionsWithAccessKey());
        using var body = CreateJsonBody(CreateInputEnvelope());

        var response = await handler.HandleAsync(
            "POST",
            CreateSignedHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}"),
            body,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        Assert.Contains("gapDetected", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsyncReturnsServiceUnavailableWhenBrokerForwardingThrows()
    {
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler((request, cancellationToken) =>
        {
            throw new HttpRequestException("connection refused");
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:5071")
        };

        var handler = CreateHandler(httpClient, CreateOptionsWithAccessKey());
        using var body = CreateJsonBody(CreateInputEnvelope());

        var response = await handler.HandleAsync(
            "POST",
            CreateSignedHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}"),
            body,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("unavailable", response.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsyncRejectsMissingSignatureWithoutCallingBroker()
    {
        var brokerCalled = false;
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler((request, cancellationToken) =>
        {
            brokerCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:5071")
        };

        var handler = CreateHandler(httpClient, CreateOptionsWithAccessKey());
        using var body = CreateJsonBody(CreateInputEnvelope());
        var headers = CreateHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}");
        headers.Add(WebPubSubUpstreamHandler.WebHookRequestOriginHeaderName, WebPubSubOrigin);
        headers.Add(WebPubSubUpstreamHandler.CloudEventConnectionIdHeaderName, "conn-123");

        var response = await handler.HandleAsync("POST", headers, body, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("signature", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.False(brokerCalled);
    }

    [Fact]
    public async Task HandleAsyncRejectsInvalidSignatureWithoutCallingBroker()
    {
        var brokerCalled = false;
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler((request, cancellationToken) =>
        {
            brokerCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:5071")
        };

        var handler = CreateHandler(httpClient, CreateOptionsWithAccessKey());
        using var body = CreateJsonBody(CreateInputEnvelope());
        var headers = CreateHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}");
        headers.Add(WebPubSubUpstreamHandler.WebHookRequestOriginHeaderName, WebPubSubOrigin);
        headers.Add(WebPubSubUpstreamHandler.CloudEventConnectionIdHeaderName, "conn-123");
        headers.Add(WebPubSubUpstreamAuthenticator.CloudEventSignatureHeaderName, "sha256=deadbeef");

        var response = await handler.HandleAsync("POST", headers, body, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("signature", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.False(brokerCalled);
    }

    [Fact]
    public async Task HandleAsyncAcceptsWebHookSignatureAlias()
    {
        HttpRequestMessage? capturedRequest = null;
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler((request, cancellationToken) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:5071")
        };

        var handler = CreateHandler(httpClient, CreateOptionsWithAccessKey());
        using var body = CreateJsonBody(CreateInputEnvelope());
        var headers = CreateHeaders($"azure.webpubsub.user.{SessionUpstreamEventNames.Input}");
        headers.Add(WebPubSubUpstreamHandler.WebHookRequestOriginHeaderName, WebPubSubOrigin);
        headers.Add(WebPubSubUpstreamHandler.CloudEventConnectionIdHeaderName, "conn-123");
        headers.Add(
            WebPubSubUpstreamAuthenticator.WebHookSignatureHeaderName,
            ComputeSignature(UpstreamAccessKey, "conn-123"));

        var response = await handler.HandleAsync("POST", headers, body, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(capturedRequest);
    }

    [Fact]
    public async Task HandleAsyncAcceptsTrustedManagedIdentityRequestWithoutSignature()
    {
        HttpRequestMessage? capturedRequest = null;
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler((request, cancellationToken) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:5071")
        };

        var handler = CreateHandler(
            httpClient,
            new FunctionsHostOptions
            {
                WebPubSubEndpoint = $"https://{WebPubSubOrigin}",
                BrokerBaseUrl = "http://127.0.0.1:5071",
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

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(capturedRequest);
    }

    [Fact]
    public async Task HandleAsyncRejectsUntrustedManagedIdentityRequest()
    {
        var brokerCalled = false;
        using var httpClient = new HttpClient(new DelegateHttpMessageHandler((request, cancellationToken) =>
        {
            brokerCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:5071")
        };

        var handler = CreateHandler(
            httpClient,
            new FunctionsHostOptions
            {
                WebPubSubEndpoint = $"https://{WebPubSubOrigin}",
                BrokerBaseUrl = "http://127.0.0.1:5071",
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

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("principal", response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.False(brokerCalled);
    }

    private static WebPubSubUpstreamHandler CreateHandler(HttpClient httpClient, FunctionsHostOptions? options = null) =>
        new(
            new WebPubSubUpstreamAuthenticator(
                Options.Create(options ?? CreateOptionsWithAccessKey()),
                NullLogger<WebPubSubUpstreamAuthenticator>.Instance),
            new BrokerInputForwarder(httpClient),
            NullLogger<WebPubSubUpstreamHandler>.Instance);

    private static HttpHeadersCollection CreateHeaders(string? cloudEventType = null)
    {
        var headers = new HttpHeadersCollection();
        if (!string.IsNullOrWhiteSpace(cloudEventType))
        {
            headers.Add(WebPubSubUpstreamHandler.CloudEventTypeHeaderName, cloudEventType);
        }

        return headers;
    }

    private static HttpHeadersCollection CreateSignedHeaders(
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

    private static FunctionsHostOptions CreateOptionsWithAccessKey() =>
        new()
        {
            WebPubSubEndpoint = $"https://{WebPubSubOrigin}",
            BrokerBaseUrl = "http://127.0.0.1:5071",
            WebPubSubUpstreamAccessKeys = [UpstreamAccessKey]
        };

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

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
