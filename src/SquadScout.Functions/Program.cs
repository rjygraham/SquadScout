using Azure.Core;
using Azure.Core.Serialization;
using Azure.Identity;
using Azure.Messaging.WebPubSub;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
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

var builder = FunctionsApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.Configure<WorkerOptions>(options =>
{
    options.Serializer = new JsonObjectSerializer(jsonOptions);
});

builder.Services
    .AddOptions<FunctionsHostOptions>()
    .Bind(builder.Configuration.GetSection(FunctionsHostOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => Uri.TryCreate(options.WebPubSubEndpoint, UriKind.Absolute, out _),
        $"{FunctionsHostOptions.SectionName}:WebPubSubEndpoint must be an absolute URI.")
    .ValidateOnStart();

builder.Services.AddSingleton<TimeProvider>(_ => TimeProvider.System);
builder.Services.AddSingleton<TokenCredential>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FunctionsHostOptions>>().Value;
    return new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ManagedIdentityClientId = options.ManagedIdentityClientId,
        ExcludeInteractiveBrowserCredential = true
    });
});

builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FunctionsHostOptions>>().Value;
    var credential = serviceProvider.GetRequiredService<TokenCredential>();
    return new WebPubSubServiceClient(new Uri(options.WebPubSubEndpoint), options.WebPubSubHub, credential);
});
builder.Services.AddSingleton<IWebPubSubAccessUriClient, ManagedIdentityWebPubSubAccessUriClient>();
builder.Services.AddSingleton<NegotiationIdentityResolver>();
builder.Services.AddSingleton<WebPubSubNegotiationService>();

builder.Build().Run();
