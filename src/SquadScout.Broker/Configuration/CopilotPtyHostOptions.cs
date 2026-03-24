using SquadScout.Contracts.Security;

namespace SquadScout.Broker.Configuration;

public sealed class CopilotPtyHostOptions
{
    public const string SectionName = "CopilotPty";

    public string ExecutablePath { get; set; } = "copilot";

    public string[] BaseArguments { get; set; } = Array.Empty<string>();

    public string WorkingDirectory { get; set; } = string.Empty;

    public int InitialRows { get; set; } = 30;

    public int InitialColumns { get; set; } = 120;

    public int OutputBufferSize { get; set; } = 1024;

    public int MaxInputCharactersPerWrite { get; set; } = PtyInputSanitizer.DefaultMaxInputCharactersPerWrite;

    public Dictionary<string, string> Environment { get; set; } = [];
}
