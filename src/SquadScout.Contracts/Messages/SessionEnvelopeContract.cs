namespace SquadScout.Contracts.Messages;

/// <summary>
/// Defines the current wire-contract version for session-scoped broker messages.
/// Within a major version, changes must remain backward compatible by adding optional members or
/// new message types only. Renaming/removing fields, changing sequence/acknowledgement semantics,
/// or changing required payload meaning requires a new major version.
/// </summary>
public static class SessionEnvelopeContract
{
    public const int CurrentVersion = 1;
}
