namespace SquadScout.Broker.Sessions;

internal sealed class SessionTelemetryBuffer<T>
{
    private readonly T?[] _items;
    private int _count;
    private int _nextWriteIndex;

    public SessionTelemetryBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        Capacity = capacity;
        _items = new T[capacity];
    }

    public int Capacity { get; }

    public int Count => _count;

    public void Append(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        _items[_nextWriteIndex] = item;
        _nextWriteIndex = (_nextWriteIndex + 1) % Capacity;

        if (_count < Capacity)
        {
            _count++;
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        if (_count == 0)
        {
            return Array.Empty<T>();
        }

        var ordered = new List<T>(_count);
        var startIndex = _count == Capacity ? _nextWriteIndex : 0;

        for (var offset = 0; offset < _count; offset++)
        {
            var item = _items[(startIndex + offset) % Capacity];
            if (item is not null)
            {
                ordered.Add(item);
            }
        }

        return ordered;
    }
}
