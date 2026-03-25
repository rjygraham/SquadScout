namespace SquadScout.Broker.Relay;

public interface IWebPubSubGroupClient
{
    Task SendJsonToGroupAsync(string sessionGroup, string jsonPayload, CancellationToken cancellationToken = default);
}
