using System.Text.RegularExpressions;
using SquadScout.Contracts.Realtime;

namespace SquadScout.Functions.Negotiation;

public sealed partial record NegotiationIdentity
{
    public string PrincipalId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string IdentityProvider { get; init; } = string.Empty;

    public bool IsDevelopment { get; init; }

    public string CreateConnectionUserId(PubSubParticipantKind participantKind, string? brokerId = null)
    {
        var prefix = participantKind == PubSubParticipantKind.Broker ? "broker" : "client";
        var segments = new List<string> { prefix, SanitizeSegment(PrincipalId) };

        if (participantKind == PubSubParticipantKind.Broker && !string.IsNullOrWhiteSpace(brokerId))
        {
            segments.Add(SanitizeSegment(brokerId));
        }

        return string.Join(':', segments);
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
}
