using Azure.Messaging.WebPubSub;

namespace SquadScout.Functions.Negotiation;

public sealed class ManagedIdentityWebPubSubAccessUriClient : IWebPubSubAccessUriClient
{
    private readonly WebPubSubServiceClient _serviceClient;

    public ManagedIdentityWebPubSubAccessUriClient(WebPubSubServiceClient serviceClient)
    {
        _serviceClient = serviceClient;
    }

    public Task<Uri> GetClientAccessUriAsync(
        DateTimeOffset expiresAt,
        string userId,
        IEnumerable<string> roles,
        IEnumerable<string> groups,
        CancellationToken cancellationToken) =>
        _serviceClient.GetClientAccessUriAsync(
            expiresAt,
            userId,
            roles,
            groups,
            cancellationToken: cancellationToken);
}
