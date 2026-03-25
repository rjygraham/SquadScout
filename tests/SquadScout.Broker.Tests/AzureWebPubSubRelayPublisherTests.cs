using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SquadScout.Broker.Relay;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests;

public sealed class AzureWebPubSubRelayPublisherTests
{
    [Fact]
    public async Task PublishEnvelopeAsyncSerializesEnvelopeToResolvedSessionGroup()
    {
        var groupClient = new RecordingWebPubSubGroupClient();
        var publisher = new AzureWebPubSubRelayPublisher(groupClient, new SessionGroupResolver(), NullLogger<AzureWebPubSubRelayPublisher>.Instance);
        var envelope = new MessageEnvelope<OutputChunkPayload>
        {
            ProjectId = "proj-01",
            SessionId = "session-abc",
            Generation = 1,
            MessageType = SessionMessageType.Output,
            Direction = MessageDirection.BrokerToClient,
            Sequence = 2,
            MessageId = "broker-output-2",
            CorrelationId = "corr-output",
            Payload = new OutputChunkPayload
            {
                Content = "ready",
                IsError = false
            }
        };

        await publisher.PublishSessionStartedAsync(new SessionDescriptor
        {
            ProjectId = envelope.ProjectId,
            SessionId = envelope.SessionId
        });
        await publisher.PublishEnvelopeAsync(envelope);

        var publication = Assert.Single(groupClient.Publications);
        Assert.Equal("session:proj-01:session-abc", publication.SessionGroup);

        using var document = JsonDocument.Parse(publication.JsonPayload);
        Assert.Equal("proj-01", document.RootElement.GetProperty("projectId").GetString());
        Assert.Equal("session-abc", document.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("output", document.RootElement.GetProperty("messageType").GetString());
    }

    private sealed record Publication(string SessionGroup, string JsonPayload);

    private sealed class RecordingWebPubSubGroupClient : IWebPubSubGroupClient
    {
        private readonly List<Publication> _publications = [];

        public IReadOnlyList<Publication> Publications => _publications;

        public Task SendJsonToGroupAsync(string sessionGroup, string jsonPayload, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _publications.Add(new Publication(sessionGroup, jsonPayload));
            return Task.CompletedTask;
        }
    }
}
