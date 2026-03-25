using System.Text.RegularExpressions;

namespace SquadScout.Contracts.Realtime;

public static partial class SessionGroupName
{
    public static string Create(string projectId, string sessionId, string? brokerId = null)
    {
        if (!TryCreate(projectId, sessionId, brokerId, out var sessionGroup, out var validationError))
        {
            throw new ArgumentException(validationError);
        }

        return sessionGroup;
    }

    public static bool TryCreate(
        string projectId,
        string sessionId,
        string? brokerId,
        out string sessionGroup,
        out string validationError)
    {
        sessionGroup = string.Empty;
        validationError = string.Empty;

        if (!TryValidateSegment(projectId, "projectId", out validationError) ||
            !TryValidateSegment(sessionId, "sessionId", out validationError) ||
            !TryValidateOptionalSegment(brokerId, "brokerId", out validationError))
        {
            return false;
        }

        sessionGroup = string.IsNullOrWhiteSpace(brokerId)
            ? $"session:{projectId}:{sessionId}"
            : $"session:{projectId}:{sessionId}:{brokerId}";

        return true;
    }

    private static bool TryValidateOptionalSegment(string? value, string parameterName, out string validationError)
    {
        validationError = string.Empty;
        return string.IsNullOrWhiteSpace(value) || TryValidateSegment(value, parameterName, out validationError);
    }

    private static bool TryValidateSegment(string value, string parameterName, out string validationError)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            validationError = $"A non-empty {parameterName} is required.";
            return false;
        }

        if (!SegmentPattern().IsMatch(value))
        {
            validationError =
                $"{parameterName} may only contain letters, numbers, '.', '_' or '-', must start with an alphanumeric character, and cannot include ':'.";
            return false;
        }

        validationError = string.Empty;
        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();
}
