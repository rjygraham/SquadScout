using SquadScout.Broker.Configuration;
using SquadScout.Broker.Pty;
using SquadScout.Broker.Projects;
using SquadScout.Broker.Realtime;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using Azure.Messaging.WebPubSub;
using SquadScout.Contracts.Messages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services
    .AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation())
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation(options =>
        {
            options.Filter = context =>
                !context.Request.Path.StartsWithSegments("/health")
                && !context.Request.Path.StartsWithSegments("/alive");
        });
    });
builder.Services.ConfigureHttpJsonOptions(static options =>
{
    SessionMessageSerializer.Configure(options.SerializerOptions);
});

var brokerOptions = builder.Configuration.GetSection(BrokerHostOptions.SectionName).Get<BrokerHostOptions>() ?? new BrokerHostOptions();
var effectiveListenUrl = builder.Configuration[WebHostDefaults.ServerUrlsKey] ?? brokerOptions.ListenUrl;
if (string.IsNullOrWhiteSpace(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
{
    builder.WebHost.UseUrls(brokerOptions.ListenUrl);
}

builder.Services.Configure<BrokerHostOptions>(builder.Configuration.GetSection(BrokerHostOptions.SectionName));
builder.Services.Configure<CopilotPtyHostOptions>(builder.Configuration.GetSection(CopilotPtyHostOptions.SectionName));
builder.Services.Configure<AzureWebPubSubOptions>(builder.Configuration.GetSection(AzureWebPubSubOptions.SectionName));
builder.Services.AddSingleton<IProjectCatalog, InMemoryProjectCatalog>();
builder.Services.AddSingleton<IPtyHost, CopilotPtyHost>();
builder.Services.AddSingleton<PtySessionEnvelopePump>();
builder.Services.AddSingleton<ISessionGroupResolver, SessionGroupResolver>();
builder.Services.AddSingleton<IRelayPublisher>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<AzureWebPubSubOptions>>().Value;
    if (string.IsNullOrWhiteSpace(options.ConnectionString))
    {
        return new NullRelayPublisher();
    }

    var groupClient = new AzureWebPubSubGroupClient(new WebPubSubServiceClient(options.ConnectionString, options.Hub));
    return new AzureWebPubSubRelayPublisher(
        groupClient,
        serviceProvider.GetRequiredService<ISessionGroupResolver>(),
        serviceProvider.GetRequiredService<ILogger<AzureWebPubSubRelayPublisher>>());
});
builder.Services.AddSingleton<ISessionRelay, InMemorySessionRelay>();
builder.Services.AddSingleton<ISequenceValidator, SessionSequenceValidator>();
builder.Services.AddSingleton<ISessionOrchestrator, InMemorySessionOrchestrator>();
builder.Services.AddSingleton<BrokerControlMessageHandler>();
builder.Services.AddSingleton<WebPubSubUpstreamAuthenticator>();
builder.Services.AddSingleton<WebPubSubUpstreamHandler>();
builder.Services.AddHostedService<BrokerControlChannelService>();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.MapHealthChecks("/alive", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("live")
    });
}

app.MapGet("/", () => Results.Ok(new
{
    service = "SquadScout Broker",
    listenUrl = effectiveListenUrl,
    localUi = "reserved for the co-hosted Blazor Server admin shell",
    relay = "reserved for Azure Web PubSub integration",
    state = "reserved for Orleans-backed session ownership"
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapMethods("/api/upstream", ["POST", "OPTIONS"], async (
    HttpContext context,
    WebPubSubUpstreamHandler handler,
    CancellationToken cancellationToken) =>
{
    var response = await handler.HandleAsync(
            context.Request.Method,
            context.Request.Headers,
            context.Request.Body,
            cancellationToken)
        .ConfigureAwait(false);

    if (!string.IsNullOrWhiteSpace(response.AllowedOrigin))
    {
        context.Response.Headers[WebPubSubUpstreamHandler.WebHookAllowedOriginHeaderName] = response.AllowedOrigin;
    }

    if (!string.IsNullOrWhiteSpace(response.ContentType))
    {
        context.Response.ContentType = response.ContentType;
    }

    context.Response.StatusCode = (int)response.StatusCode;
    if (!string.IsNullOrWhiteSpace(response.Body))
    {
        await context.Response.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
    }
});

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

app.MapPost("/api/sessions", async (SquadScout.Contracts.Sessions.StartSessionCommand command, ISessionRelay relay, CancellationToken cancellationToken) =>
{
    try
    {
        var session = await relay.StartAsync(command, cancellationToken);
        return Results.Accepted($"/api/sessions/{session.SessionId}", session);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (SessionControlException ex)
    {
        return SessionControlError(ex);
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Session start failed", detail: ex.Message);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Session start failed", detail: ex.Message);
    }
    catch (PtySessionStartException ex)
    {
        return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Session start failed", detail: ex.Message);
    }
});

app.MapGet("/api/sessions/{sessionId}", async (string sessionId, ISessionOrchestrator orchestrator, CancellationToken cancellationToken) =>
{
    var session = await orchestrator.GetAsync(sessionId, cancellationToken);
    return session is null ? Results.NotFound() : Results.Ok(session);
});

app.MapGet("/api/sessions/{sessionId}/telemetry", async (
    string sessionId,
    ISessionOrchestrator orchestrator,
    CancellationToken cancellationToken) =>
{
    try
    {
        var telemetry = await orchestrator.ExportTelemetryAsync(sessionId, cancellationToken);
        return Results.Ok(telemetry);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { message = ex.Message });
    }
});

app.MapPost("/api/sessions/{sessionId}/stop", async (
    string sessionId,
    SquadScout.Contracts.Sessions.StopSessionCommand command,
    ISessionRelay relay,
    CancellationToken cancellationToken) =>
{
    try
    {
        var session = await relay.StopAsync(sessionId, command, cancellationToken);
        return Results.Ok(session);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (SessionControlException ex)
    {
        return SessionControlError(ex);
    }
});

app.MapPost("/api/sessions/{sessionId}/input", async (
    string sessionId,
    MessageEnvelope<InputChunkPayload> envelope,
    ISessionRelay relay,
    CancellationToken cancellationToken) =>
{
    try
    {
        var validation = await relay.RelayInputAsync(sessionId, envelope, cancellationToken);
        return validation.Status switch
        {
            SequenceValidationStatus.Accepted or SequenceValidationStatus.Duplicate or SequenceValidationStatus.GapDetected => Results.Ok(validation),
            SequenceValidationStatus.StaleGeneration or SequenceValidationStatus.FutureGeneration => Results.Conflict(validation),
            SequenceValidationStatus.InvalidEnvelope => Results.BadRequest(validation),
            _ => Results.BadRequest(validation)
        };
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (SessionControlException ex)
    {
        return SessionControlError(ex);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { message = ex.Message });
    }
});

app.Run();

static IResult SessionControlError(SessionControlException ex) =>
    Results.Json(
        new
        {
            code = ex.Code,
            message = ex.Message,
            sessionId = ex.SessionId,
            projectId = ex.ProjectId,
            state = ex.SessionState?.ToString()
        },
        statusCode: ex.StatusCode);

public partial class Program;
