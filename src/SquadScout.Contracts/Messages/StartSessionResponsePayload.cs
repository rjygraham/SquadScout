using SquadScout.Contracts.Sessions;

namespace SquadScout.Contracts.Messages;

public sealed record StartSessionResponsePayload
{
    public SessionDescriptor? Session { get; init; }

    public string Summary { get; init; } = string.Empty;

    public string? Error { get; init; }
}
