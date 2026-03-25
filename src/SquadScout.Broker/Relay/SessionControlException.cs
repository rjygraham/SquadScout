using SquadScout.Contracts.Sessions;

namespace SquadScout.Broker.Relay;

public sealed class SessionControlException : Exception
{
    public SessionControlException(
        string code,
        int statusCode,
        string message,
        string sessionId,
        string? projectId = null,
        SessionState? sessionState = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("An error code is required.", nameof(code))
            : code;
        StatusCode = statusCode;
        SessionId = string.IsNullOrWhiteSpace(sessionId)
            ? throw new ArgumentException("A session id is required.", nameof(sessionId))
            : sessionId;
        ProjectId = projectId;
        SessionState = sessionState;
    }

    public string Code { get; }

    public int StatusCode { get; }

    public string SessionId { get; }

    public string? ProjectId { get; }

    public SessionState? SessionState { get; }
}
