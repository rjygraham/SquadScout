using System.Text.Json;
using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Sessions;

public sealed class CircularReplayBuffer
{
    private readonly MessageEnvelope<JsonElement>?[] _buffer;
    private int _count;
    private int _nextWriteIndex;

    public CircularReplayBuffer(int capacity = SessionSequencingDefaults.ReplayBufferCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        Capacity = capacity;
        _buffer = new MessageEnvelope<JsonElement>[capacity];
    }

    public int Capacity { get; }

    public int Count => _count;

    public long? AvailableFromSequence => TryGetOrderedItems().FirstOrDefault()?.Sequence;

    public long? AvailableToSequence => TryGetOrderedItems().LastOrDefault()?.Sequence;

    public void Append(MessageEnvelope<JsonElement> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Direction != MessageDirection.BrokerToClient)
        {
            throw new ArgumentException("Replay buffer only stores broker-to-client envelopes.", nameof(envelope));
        }

        if (envelope.Sequence is null)
        {
            throw new ArgumentException("Replay buffer only stores sequenced envelopes.", nameof(envelope));
        }

        _buffer[_nextWriteIndex] = envelope;
        _nextWriteIndex = (_nextWriteIndex + 1) % Capacity;

        if (_count < Capacity)
        {
            _count++;
        }
    }

    public void Clear()
    {
        Array.Clear(_buffer);
        _count = 0;
        _nextWriteIndex = 0;
    }

    public ReplayBufferReadResult ReadWindow(long fromSequenceInclusive, long? toSequenceInclusive, int maximumMessages)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromSequenceInclusive);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessages);
        if (toSequenceInclusive is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toSequenceInclusive), "To sequence must be positive when specified.");
        }

        var orderedItems = TryGetOrderedItems();
        if (orderedItems.Count == 0)
        {
            return new ReplayBufferReadResult();
        }

        var availableFrom = orderedItems[0].Sequence!.Value;
        var availableTo = orderedItems[^1].Sequence!.Value;
        var gapDetected = fromSequenceInclusive < availableFrom;
        var effectiveFrom = Math.Max(fromSequenceInclusive, availableFrom);
        var effectiveTo = Math.Min(toSequenceInclusive ?? long.MaxValue, availableTo);

        if (effectiveFrom > effectiveTo)
        {
            return new ReplayBufferReadResult
            {
                AvailableFromSequence = availableFrom,
                AvailableToSequence = availableTo,
                GapDetected = gapDetected
            };
        }

        var messages = orderedItems
            .Where(message => message.Sequence >= effectiveFrom && message.Sequence <= effectiveTo)
            .Take(maximumMessages)
            .ToArray();

        var hasMore = messages.Length > 0 && messages[^1].Sequence!.Value < effectiveTo;

        return new ReplayBufferReadResult
        {
            AvailableFromSequence = availableFrom,
            AvailableToSequence = availableTo,
            GapDetected = gapDetected,
            HasMore = hasMore,
            Messages = messages
        };
    }

    private IReadOnlyList<MessageEnvelope<JsonElement>> TryGetOrderedItems()
    {
        if (_count == 0)
        {
            return Array.Empty<MessageEnvelope<JsonElement>>();
        }

        var orderedItems = new List<MessageEnvelope<JsonElement>>(_count);
        var startIndex = _count == Capacity ? _nextWriteIndex : 0;

        for (var offset = 0; offset < _count; offset++)
        {
            var item = _buffer[(startIndex + offset) % Capacity];
            if (item is not null)
            {
                orderedItems.Add(item);
            }
        }

        return orderedItems;
    }
}
