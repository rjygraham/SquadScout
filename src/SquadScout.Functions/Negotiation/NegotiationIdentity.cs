using System.Text.RegularExpressions;
using SquadScout.Contracts.Realtime;

namespace SquadScout.Functions.Negotiation;

public sealed partial record NegotiationIdentity
{
    public string PrincipalId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string IdentityProvider { get; init; } = string.Empty;

    public bool IsDevelopment { get; init; }

    public string CreateConnectionUserId(
        PubSubParticipantKind participantKind,
        string projectId,
        string sessionId,
        string? brokerId = null)
    {
        if (!Enum.IsDefined(participantKind))
        {
            throw new ArgumentOutOfRangeException(nameof(participantKind), participantKind, "participantKind must be either Client or Broker.");
        }

        var prefix = participantKind == PubSubParticipantKind.Broker ? "broker" : "client";
        var segments = new List<string>
        {
            prefix,
            SanitizeSegment(projectId),
            SanitizeSegment(sessionId)
        };

        if (!string.IsNullOrWhiteSpace(brokerId))
        {
            segments.Add(SanitizeSegment(brokerId));
        }

        segments.Add(SanitizeSegment(PrincipalId));
        return string.Join(':', segments);
    }

    public bool MatchesAuthenticatedPrincipal(NegotiationIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(PrincipalId, other.PrincipalId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(IdentityProvider, other.IdentityProvider, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryValidateTrustedIdentity(
        string? principalId,
        string? identityProvider,
        out string validationError)
    {
        if (!TryValidateRequiredSegment(principalId, "principalId", out validationError) ||
            !TryValidateRequiredSegment(identityProvider, "identityProvider", out validationError))
        {
            return false;
        }

        validationError = string.Empty;
        return true;
    }

    private static bool TryValidateRequiredSegment(string? value, string parameterName, out string validationError)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            validationError = $"A non-empty {parameterName} is required.";
            return false;
        }

        if (!TrustedIdentitySegmentPattern().IsMatch(value.Trim()))
        {
            validationError =
                $"{parameterName} may only contain letters, numbers, '.', '_' or '-', and must start with an alphanumeric character.";
            return false;
        }

        validationError = string.Empty;
        return true;
    }

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "anonymous";
        }

        var sanitized = UnsafeUserIdSegmentPattern().Replace(value.Trim(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "anonymous" : sanitized;
    }

    [GeneratedRegex("[^A-Za-z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeUserIdSegmentPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex TrustedIdentitySegmentPattern();
}
