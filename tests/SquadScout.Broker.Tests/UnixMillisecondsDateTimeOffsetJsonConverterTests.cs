using System.Text.Json;
using SquadScout.Contracts.Messages;

namespace SquadScout.Broker.Tests;

public sealed class UnixMillisecondsDateTimeOffsetJsonConverterTests
{
    [Theory]
    [InlineData(-62135596800001L)]
    [InlineData(253402300800000L)]
    public void OutOfRangeUnixMillisecondsThrowJsonException(long milliseconds)
    {
        var json = $$"""
            {
              "timestampUtc": {{milliseconds}}
            }
            """;

        var exception = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<TimestampEnvelope>(json, SessionMessageSerializer.DefaultOptions));

        Assert.Contains("timestampUtc", exception.Path);
    }

    private sealed class TimestampEnvelope
    {
        public DateTimeOffset TimestampUtc { get; init; }
    }
}
