using System.Text.Json;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Tests;

public sealed class CircularReplayBufferTests
{
    [Fact]
    public void AppendsMessagesInOrderAndEvictsOldestOnOverflow()
    {
        var buffer = new CircularReplayBuffer(3);

        buffer.Append(CreateOutputSnapshot(1));
        buffer.Append(CreateOutputSnapshot(2));
        buffer.Append(CreateOutputSnapshot(3));
        buffer.Append(CreateOutputSnapshot(4));

        var window = buffer.ReadWindow(1, null, 10);

        Assert.Equal(3, buffer.Count);
        Assert.Equal(2, buffer.AvailableFromSequence);
        Assert.Equal(4, buffer.AvailableToSequence);
        Assert.True(window.GapDetected);
        Assert.Collection(
            window.Messages,
            message => Assert.Equal(2, message.Sequence),
            message => Assert.Equal(3, message.Sequence),
            message => Assert.Equal(4, message.Sequence));
    }

    [Fact]
    public void ReadWindowReturnsOnlyAvailableOverlapWhenRequestedRangeExpired()
    {
        var buffer = new CircularReplayBuffer(3);

        buffer.Append(CreateOutputSnapshot(3));
        buffer.Append(CreateOutputSnapshot(4));
        buffer.Append(CreateOutputSnapshot(5));

        var window = buffer.ReadWindow(1, 4, 10);

        Assert.True(window.GapDetected);
        Assert.Equal(3, window.AvailableFromSequence);
        Assert.Equal(5, window.AvailableToSequence);
        Assert.Collection(
            window.Messages,
            message => Assert.Equal(3, message.Sequence),
            message => Assert.Equal(4, message.Sequence));
    }

    [Fact]
    public void ReadWindowPaginatesRequestedRange()
    {
        var buffer = new CircularReplayBuffer(5);

        buffer.Append(CreateOutputSnapshot(1));
        buffer.Append(CreateOutputSnapshot(2));
        buffer.Append(CreateOutputSnapshot(3));
        buffer.Append(CreateOutputSnapshot(4));

        var window = buffer.ReadWindow(2, 4, 2);

        Assert.False(window.GapDetected);
        Assert.Equal(2, window.FromSequenceInclusive);
        Assert.Equal(3, window.ToSequenceInclusive);
        Assert.True(window.HasMore);
        Assert.False(window.IsComplete);
    }

    [Fact]
    public void ReadWindowRejectsNonPositiveUpperBounds()
    {
        var buffer = new CircularReplayBuffer(3);
        buffer.Append(CreateOutputSnapshot(1));

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => buffer.ReadWindow(1, 0, 10));

        Assert.Equal("toSequenceInclusive", exception.ParamName);
    }

    private static MessageEnvelope<JsonElement> CreateOutputSnapshot(long sequence) =>
        new()
        {
            ProjectId = "broker",
            SessionId = "session-123",
            Generation = SessionEnvelopeContract.InitialGeneration,
            MessageType = SessionMessageType.Output,
            Direction = MessageDirection.BrokerToClient,
            Sequence = sequence,
            MessageId = $"msg-{sequence}",
            CorrelationId = "corr-output",
            Payload = JsonSerializer.SerializeToElement(new { text = $"line-{sequence}" }, SessionMessageSerializer.DefaultOptions)
        };
}
