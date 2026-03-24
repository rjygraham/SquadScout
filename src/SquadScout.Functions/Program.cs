using Azure.Core;
using Azure.Core.Serialization;
using Azure.Identity;
using Azure.Messaging.WebPubSub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Text.Json.Serialization;
using SquadScout.Functions.Configuration;
using SquadScout.Functions.Negotiation;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};
jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(worker =>
    {
        worker.Serializer = new JsonObjectSerializer(jsonOptions);
    })
    .ConfigureServices((context, services) =>
    {
        services
            .AddOptions<FunctionsHostOptions>()
            .Bind(context.Configuration.GetSection(FunctionsHostOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => Uri.TryCreate(options.WebPubSubEndpoint, UriKind.Absolute, out _),
                $"{FunctionsHostOptions.SectionName}:WebPubSubEndpoint must be an absolute URI.")
            .ValidateOnStart();

        services.AddSingleton<TimeProvider>(_ => TimeProvider.System);
        services.AddSingleton<TokenCredential>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FunctionsHostOptions>>().Value;
            return new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = options.ManagedIdentityClientId,
                ExcludeInteractiveBrowserCredential = true
            });
        });

        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FunctionsHostOptions>>().Value;
            var credential = serviceProvider.GetRequiredService<TokenCredential>();
            return new WebPubSubServiceClient(new Uri(options.WebPubSubEndpoint), options.WebPubSubHub, credential);
        });
        services.AddSingleton<IWebPubSubAccessUriClient, ManagedIdentityWebPubSubAccessUriClient>();
        services.AddSingleton<NegotiationIdentityResolver>();
        services.AddSingleton<WebPubSubNegotiationService>();
    })
    .Build();

host.Run();
