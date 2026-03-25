namespace SquadScout.Functions.Negotiation;

public interface IWebPubSubAccessUriClient
{
    Task<Uri> GetClientAccessUriAsync(
        DateTimeOffset expiresAt,
        string userId,
        IEnumerable<string> roles,
        IEnumerable<string> groups,
        CancellationToken cancellationToken);
}
