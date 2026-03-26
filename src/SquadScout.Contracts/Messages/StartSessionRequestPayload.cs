using SquadScout.Contracts.Sessions;

namespace SquadScout.Contracts.Messages;

public sealed record StartSessionRequestPayload
{
    public StartSessionCommand Command { get; init; } = new();
}
