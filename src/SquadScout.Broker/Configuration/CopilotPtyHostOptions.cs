using SquadScout.Contracts.Security;

namespace SquadScout.Broker.Configuration;

public sealed class CopilotPtyHostOptions
{
    public const string SectionName = "CopilotPty";

    public string ExecutablePath { get; set; } = "copilot";

    public string[] BaseArguments { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the fallback working directory for Copilot PTY sessions when a project does not have a configured RepositoryRoot.
    /// This value is only used if the project's RepositoryRoot is null or empty.
    /// If this value is also empty, the broker's current working directory is used.
    /// In normal operation, each session uses its project's RepositoryRoot as the working directory.
    /// </summary>
    /// <remarks>
    /// Working directory resolution priority:
    /// 1. Project RepositoryRoot (set via project registration)
    /// 2. This fallback value (CopilotPtyHostOptions.WorkingDirectory)
    /// 3. Current broker process directory (Environment.CurrentDirectory)
    /// </remarks>
    public string WorkingDirectory { get; set; } = string.Empty;

    public int InitialRows { get; set; } = 30;

    public int InitialColumns { get; set; } = 120;

    public int OutputBufferSize { get; set; } = 1024;

    public int MaxInputCharactersPerWrite { get; set; } = PtyInputSanitizer.DefaultMaxInputCharactersPerWrite;

    public Dictionary<string, string> Environment { get; set; } = [];
}
