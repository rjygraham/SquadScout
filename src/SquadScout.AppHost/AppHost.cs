var builder = DistributedApplication.CreateBuilder(args);

var broker = builder.AddProject<Projects.SquadScoutBroker>("broker", launchProfileName: null)
    .WithHttpEndpoint(name: "http", port: 5071, isProxied: false)
    .WithHttpHealthCheck("/health");

builder.AddAzureFunctionsProject<Projects.SquadScoutFunctions>("functions");

builder.AddMauiProject("app", @"..\SquadScout.App\SquadScout.App.csproj")
    .AddWindowsDevice()
    .WithReference(broker)
    .WithEnvironment("SQUADSCOUT_BROKERAPI__BASEURL", broker.GetEndpoint("http"));

builder.Build().Run();
