namespace SquadScout.Broker.Pty;

public sealed class PtySessionStartException : Exception
{
    public PtySessionStartException(
        string sessionId,
        string projectId,
        string executablePath,
        string workingDirectory,
        Exception innerException)
        : base(
            $"Unable to start PTY session '{sessionId}' for project '{projectId}' using '{executablePath}' in '{workingDirectory}'.",
            innerException)
    {
        SessionId = sessionId;
        ProjectId = projectId;
        ExecutablePath = executablePath;
        WorkingDirectory = workingDirectory;
    }

    public string SessionId { get; }

    public string ProjectId { get; }

    public string ExecutablePath { get; }

    public string WorkingDirectory { get; }
}
