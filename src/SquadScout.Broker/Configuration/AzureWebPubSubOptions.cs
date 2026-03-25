namespace SquadScout.Broker.Configuration;

public sealed class AzureWebPubSubOptions
{
    public const string SectionName = "AzureWebPubSub";

    public string Hub { get; set; } = "squadscout";

    public string? ConnectionString { get; set; }
}
