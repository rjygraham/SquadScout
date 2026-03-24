using System.Text.Json;
using SquadScout.Broker.Relay;
using SquadScout.Contracts.Messages;
using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Tests.TestDoubles;

public sealed class RecordingRelayPublisher : IRelayPublisher
{
    private readonly object _syncRoot = new();
    private readonly List<MessageEnvelope<JsonElement>> _publishedEnvelopes = [];
    private readonly List<SessionDescriptor> _startedSessions = [];

    public IReadOnlyList<SessionDescriptor> StartedSessions
    {
        get
        {
            lock (_syncRoot)
            {
                return _startedSessions.ToArray();
            }
        }
    }

    public IReadOnlyList<MessageEnvelope<JsonElement>> PublishedEnvelopes
    {
        get
        {
            lock (_syncRoot)
            {
                return _publishedEnvelopes.ToArray();
            }
        }
    }

    public Task PublishSessionStartedAsync(SessionDescriptor session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);

        lock (_syncRoot)
        {
            _startedSessions.Add(session);
        }

        return Task.CompletedTask;
    }

    public Task PublishEnvelopeAsync<TPayload>(MessageEnvelope<TPayload> envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(envelope);

        lock (_syncRoot)
        {
            _publishedEnvelopes.Add(ToSnapshot(envelope));
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<MessageEnvelope<JsonElement>>> WaitForEnvelopeCountAsync(
        int expectedCount,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (expectedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedCount), expectedCount, "Expected count must be non-negative.");
        }

        var deadlineUtc = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow <= deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var envelopes = PublishedEnvelopes;
            if (envelopes.Count >= expectedCount)
            {
                return envelopes;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} published envelope(s).");
    }

    private static MessageEnvelope<JsonElement> ToSnapshot<TPayload>(MessageEnvelope<TPayload> envelope) =>
        new()
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
            Payload = envelope.Payload is JsonElement payload
                ? payload.Clone()
                : JsonSerializer.SerializeToElement(envelope.Payload, SessionMessageSerializer.DefaultOptions)
        };
}
