using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SquadScout.Functions.Configuration;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services
            .AddOptions<FunctionsHostOptions>()
            .Bind(context.Configuration.GetSection(FunctionsHostOptions.SectionName));
    })
    .Build();

host.Run();
