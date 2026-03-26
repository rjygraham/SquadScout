using Azure;
using Azure.Core;
using Azure.Messaging.WebPubSub;

namespace SquadScout.Broker.Relay;

public sealed class AzureWebPubSubGroupClient : IWebPubSubGroupClient
{
    private static readonly ContentType JsonContentType = new("application/json");
    private readonly WebPubSubServiceClient _serviceClient;

    public AzureWebPubSubGroupClient(WebPubSubServiceClient serviceClient)
    {
        _serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
    }

    public Task SendJsonToGroupAsync(string sessionGroup, string jsonPayload, CancellationToken cancellationToken = default) =>
        _serviceClient.SendToGroupAsync(
            sessionGroup,
            RequestContent.Create(jsonPayload),
            JsonContentType,
            [],
            new Azure.RequestContext
            {
                CancellationToken = cancellationToken
            });
}
