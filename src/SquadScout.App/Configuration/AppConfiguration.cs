using System.Text.Json;
using SquadScout.Contracts.Projects;

namespace SquadScout.App.Configuration;

public static class AppConfiguration
{
    private const string BaseSettingsResourceName = "SquadScout.App.appsettings.json";
    private const string DevelopmentSettingsResourceName = "SquadScout.App.appsettings.Development.json";

    public static AppEnvironment LoadEnvironment()
    {
        var environmentName = Environment.GetEnvironmentVariable("SQUADSCOUT_APP_ENVIRONMENT");

#if DEBUG
        environmentName ??= AppEnvironment.DevelopmentName;
#else
        environmentName ??= AppEnvironment.ProductionName;
#endif

        return new AppEnvironment(environmentName);
    }

    public static AppBootstrapOptions LoadOptions(AppEnvironment environment)
    {
        var resourceName = environment.IsDevelopment
            ? DevelopmentSettingsResourceName
            : BaseSettingsResourceName;

        using var stream = typeof(AppConfiguration).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded configuration resource '{resourceName}' was not found.");

        var options = JsonSerializer.Deserialize<AppBootstrapOptions>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new AppBootstrapOptions();

        ApplyEnvironmentVariableOverrides(options);
        return options;
    }

    public static Uri CreateBrokerBaseUri(BrokerApiOptions options)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var brokerBaseUri))
        {
            throw new InvalidOperationException($"BrokerApi:BaseUrl '{options.BaseUrl}' is not a valid absolute URI.");
        }

        return brokerBaseUri;
    }

    private static void ApplyEnvironmentVariableOverrides(AppBootstrapOptions options)
    {
        var brokerBaseUrl = Environment.GetEnvironmentVariable("SQUADSCOUT_BROKERAPI__BASEURL");
        if (!string.IsNullOrWhiteSpace(brokerBaseUrl))
        {
            options.BrokerApi.BaseUrl = brokerBaseUrl;
        }

        var requestedBy = Environment.GetEnvironmentVariable("SQUADSCOUT_AUTH__DEFAULTREQUESTEDBY");
        if (!string.IsNullOrWhiteSpace(requestedBy))
        {
            options.Auth.DefaultRequestedBy = requestedBy;
        }

        var hub = Environment.GetEnvironmentVariable("SQUADSCOUT_MESSAGING__HUB");
        if (!string.IsNullOrWhiteSpace(hub))
        {
            options.Messaging.Hub = hub;
        }

        var negotiateUrl = Environment.GetEnvironmentVariable("SQUADSCOUT_MESSAGING__NEGOTIATEURL");
        if (!string.IsNullOrWhiteSpace(negotiateUrl))
        {
            options.Messaging.NegotiateUrl = negotiateUrl;
        }

        var connectTimeout = Environment.GetEnvironmentVariable("SQUADSCOUT_MESSAGING__CONNECTTIMEOUTSECONDS");
        if (int.TryParse(connectTimeout, out var connectTimeoutSeconds) && connectTimeoutSeconds > 0)
        {
            options.Messaging.ConnectTimeoutSeconds = connectTimeoutSeconds;
        }

        var ackTimeout = Environment.GetEnvironmentVariable("SQUADSCOUT_MESSAGING__COMMANDACKTIMEOUTSECONDS");
        if (int.TryParse(ackTimeout, out var commandAckTimeoutSeconds) && commandAckTimeoutSeconds > 0)
        {
            options.Messaging.CommandAckTimeoutSeconds = commandAckTimeoutSeconds;
        }

        var tokenRefreshRetryCount = Environment.GetEnvironmentVariable("SQUADSCOUT_MESSAGING__TOKENREFRESHRETRYCOUNT");
        if (int.TryParse(tokenRefreshRetryCount, out var refreshRetryCount) && refreshRetryCount >= 0)
        {
            options.Messaging.TokenRefreshRetryCount = refreshRetryCount;
        }

        var tokenRefreshRetryBaseDelay = Environment.GetEnvironmentVariable("SQUADSCOUT_MESSAGING__TOKENREFRESHRETRYBASEDELAYSECONDS");
        if (int.TryParse(tokenRefreshRetryBaseDelay, out var refreshRetryBaseDelaySeconds) && refreshRetryBaseDelaySeconds > 0)
        {
            options.Messaging.TokenRefreshRetryBaseDelaySeconds = refreshRetryBaseDelaySeconds;
        }

        var tokenRefreshRetryMaxDelay = Environment.GetEnvironmentVariable("SQUADSCOUT_MESSAGING__TOKENREFRESHRETRYMAXDELAYSECONDS");
        if (int.TryParse(tokenRefreshRetryMaxDelay, out var refreshRetryMaxDelaySeconds) && refreshRetryMaxDelaySeconds > 0)
        {
            options.Messaging.TokenRefreshRetryMaxDelaySeconds = refreshRetryMaxDelaySeconds;
        }
    }
}

public sealed class AppBootstrapOptions
{
    public BrokerApiOptions BrokerApi { get; set; } = new();

    public AuthOptions Auth { get; set; } = new();

    public MessagingOptions Messaging { get; set; } = new();

    public LocalDevelopmentOptions LocalDevelopment { get; set; } = new();
}

public sealed class AppEnvironment
{
    public const string DevelopmentName = "Development";
    public const string ProductionName = "Production";

    public AppEnvironment(string name)
    {
        Name = string.IsNullOrWhiteSpace(name) ? ProductionName : name;
    }

    public string Name { get; }

    public bool IsDevelopment => string.Equals(Name, DevelopmentName, StringComparison.OrdinalIgnoreCase);
}

public sealed class BrokerApiOptions
{
    public const string SectionName = "BrokerApi";

    public string BaseUrl { get; set; } = "http://127.0.0.1:5071";

    public int RequestTimeoutSeconds { get; set; } = 10;
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Mode { get; set; } = "BrokerNegotiated";

    public string DefaultRequestedBy { get; set; } = "mobile-user";
}

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public string Hub { get; set; } = "squadscout";

    public string NegotiateUrl { get; set; } = "http://127.0.0.1:7071/api/negotiate";

    public bool AutoPrepareOnSessionStart { get; set; } = true;

    public int ConnectTimeoutSeconds { get; set; } = 15;

    public int CommandAckTimeoutSeconds { get; set; } = 10;

    public int TokenRefreshRetryCount { get; set; } = 4;

    public int TokenRefreshRetryBaseDelaySeconds { get; set; } = 5;

    public int TokenRefreshRetryMaxDelaySeconds { get; set; } = 60;

    public int RecentTrafficCapacity { get; set; } = 200;
}

public sealed class LocalDevelopmentOptions
{
    public const string SectionName = "LocalDevelopment";

    public bool UseSampleProjectsWhenBrokerUnavailable { get; set; }

    public bool CreateOfflineSessionsWhenBrokerUnavailable { get; set; }

    public List<SeedProjectOptions> SeedProjects { get; set; } = [];
}

public sealed class SeedProjectOptions
{
    public string ProjectId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string RepositoryRoot { get; set; } = string.Empty;

    public RegisteredProject ToRegisteredProject() =>
        new()
        {
            ProjectId = ProjectId,
            DisplayName = DisplayName,
            RepositoryRoot = RepositoryRoot
        };
}
