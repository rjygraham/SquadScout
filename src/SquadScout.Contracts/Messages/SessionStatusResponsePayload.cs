using SquadScout.Contracts.Sessions;

namespace SquadScout.Contracts.Messages;

public sealed record SessionStatusResponsePayload
{
    public SessionDescriptor? Session { get; init; }

    public string? Error { get; init; }
}
