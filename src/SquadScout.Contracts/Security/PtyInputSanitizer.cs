namespace SquadScout.Contracts.Security;

/// <summary>
/// Applies the day-1 PTY input safety baseline before text crosses the broker → PTY trust boundary.
/// </summary>
public static class PtyInputSanitizer
{
    public const int DefaultMaxInputCharactersPerWrite = 4096;

    public static string Sanitize(string input, int maxInputCharactersPerWrite = DefaultMaxInputCharactersPerWrite)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (maxInputCharactersPerWrite <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxInputCharactersPerWrite),
                maxInputCharactersPerWrite,
                "The PTY input limit must be greater than zero.");
        }

        var normalized = NormalizeLineEndings(input);
        if (normalized.Length > maxInputCharactersPerWrite)
        {
            throw new ArgumentException(
                $"PTY input exceeded the {maxInputCharactersPerWrite} character safety limit.",
                nameof(input));
        }

        for (var index = 0; index < normalized.Length; index++)
        {
            var current = normalized[index];
            if (IsAllowed(current))
            {
                continue;
            }

            throw new ArgumentException(
                $"PTY input contains an unsupported control character (U+{(int)current:X4}) at index {index}.",
                nameof(input));
        }

        return normalized;
    }

    private static bool IsAllowed(char value) =>
        !char.IsControl(value) ||
        value is '\n' or '\t' or '\b' or '\u001B';

    private static string NormalizeLineEndings(string input) =>
        input.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
}
