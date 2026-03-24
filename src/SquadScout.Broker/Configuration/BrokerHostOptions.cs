namespace SquadScout.Broker.Configuration;

public sealed class BrokerHostOptions
{
    public const string SectionName = "Broker";

    public string ListenUrl { get; set; } = "http://127.0.0.1:5071";

    public string ProjectRegistryPath { get; set; } = ".squadscout\\projects.json";
}
