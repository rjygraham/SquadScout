namespace SquadScout.Functions.Configuration;

public sealed class FunctionsHostOptions
{
    public const string SectionName = "Functions";

    public string WebPubSubHub { get; set; } = "squadscout";
}
