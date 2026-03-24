using SquadScout.Broker.Configuration;
using SquadScout.Broker.Projects;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;

var builder = WebApplication.CreateBuilder(args);

var brokerOptions = builder.Configuration.GetSection(BrokerHostOptions.SectionName).Get<BrokerHostOptions>() ?? new BrokerHostOptions();
builder.WebHost.UseUrls(brokerOptions.ListenUrl);

builder.Services.Configure<BrokerHostOptions>(builder.Configuration.GetSection(BrokerHostOptions.SectionName));
builder.Services.AddSingleton<IProjectCatalog, InMemoryProjectCatalog>();
builder.Services.AddSingleton<IRelayPublisher, NullRelayPublisher>();
builder.Services.AddSingleton<ISessionOrchestrator, InMemorySessionOrchestrator>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "SquadScout Broker",
    listenUrl = brokerOptions.ListenUrl,
    localUi = "reserved for the co-hosted Blazor Server admin shell",
    relay = "reserved for Azure Web PubSub integration",
    state = "reserved for Orleans-backed session ownership"
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/projects", async (IProjectCatalog catalog, CancellationToken cancellationToken) =>
{
    var projects = await catalog.ListAsync(cancellationToken);
    return Results.Ok(projects);
});

app.MapPost("/api/projects", async (SquadScout.Contracts.Projects.RegisteredProject project, IProjectCatalog catalog, CancellationToken cancellationToken) =>
{
    await catalog.UpsertAsync(project, cancellationToken);
    return Results.Accepted($"/api/projects/{project.ProjectId}", project);
});

app.MapPost("/api/sessions", async (SquadScout.Contracts.Sessions.StartSessionCommand command, ISessionOrchestrator orchestrator, CancellationToken cancellationToken) =>
{
    var session = await orchestrator.StartAsync(command, cancellationToken);
    return Results.Accepted($"/api/sessions/{session.SessionId}", session);
});

app.MapGet("/api/sessions/{sessionId}", async (string sessionId, ISessionOrchestrator orchestrator, CancellationToken cancellationToken) =>
{
    var session = await orchestrator.GetAsync(sessionId, cancellationToken);
    return session is null ? Results.NotFound() : Results.Ok(session);
});

app.Run();

public partial class Program;
