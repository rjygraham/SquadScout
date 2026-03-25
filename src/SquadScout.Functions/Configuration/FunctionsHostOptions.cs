using System.ComponentModel.DataAnnotations;

namespace SquadScout.Functions.Configuration;

public sealed class FunctionsHostOptions
{
    public const string SectionName = "Functions";

    [Required]
    public string WebPubSubEndpoint { get; set; } = string.Empty;

    public string WebPubSubHub { get; set; } = "squadscout";

    [Required]
    public string BrokerBaseUrl { get; set; } = "http://127.0.0.1:5071";

    [Range(1, 1440)]
    public int TokenLifetimeMinutes { get; set; } = 60;

    public string? ManagedIdentityClientId { get; set; }

    public bool EnableLocalDevelopmentIdentity { get; set; }

    public string DevelopmentIdentityProvider { get; set; } = "local";

    public string DevelopmentUserId { get; set; } = "local-dev";

    public string DevelopmentUserDisplayName { get; set; } = "Local Developer";
}
