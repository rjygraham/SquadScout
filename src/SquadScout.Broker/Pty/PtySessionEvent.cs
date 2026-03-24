namespace SquadScout.Broker.Pty;

public sealed record PtySessionEvent
{
    public PtySessionEventKind Kind { get; init; }

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public string? Content { get; init; }

    public bool IsError { get; init; }

    public int? ExitCode { get; init; }

    public static PtySessionEvent Started(DateTimeOffset? timestampUtc = null) =>
        new()
        {
            Kind = PtySessionEventKind.Started,
            TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow
        };

    public static PtySessionEvent Output(string content, bool isError = false, DateTimeOffset? timestampUtc = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        return new PtySessionEvent
        {
            Kind = PtySessionEventKind.Output,
            TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow,
            Content = content,
            IsError = isError
        };
    }

    public static PtySessionEvent Exited(int? exitCode, DateTimeOffset? timestampUtc = null) =>
        new()
        {
            Kind = PtySessionEventKind.Exited,
            TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow,
            ExitCode = exitCode
        };
}
