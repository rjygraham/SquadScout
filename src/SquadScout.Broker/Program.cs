using SquadScout.Broker.Configuration;
using SquadScout.Broker.Pty;
using SquadScout.Broker.Projects;
using SquadScout.Broker.Relay;
using SquadScout.Broker.Sessions;
using SquadScout.Contracts.Messages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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

var brokerOptions = builder.Configuration.GetSection(BrokerHostOptions.SectionName).Get<BrokerHostOptions>() ?? new BrokerHostOptions();
if (string.IsNullOrWhiteSpace(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
{
    builder.WebHost.UseUrls(brokerOptions.ListenUrl);
}

builder.Services.Configure<BrokerHostOptions>(builder.Configuration.GetSection(BrokerHostOptions.SectionName));
builder.Services.Configure<CopilotPtyHostOptions>(builder.Configuration.GetSection(CopilotPtyHostOptions.SectionName));
builder.Services.AddSingleton<IProjectCatalog, InMemoryProjectCatalog>();
builder.Services.AddSingleton<IPtyHost, CopilotPtyHost>();
builder.Services.AddSingleton<PtySessionEnvelopePump>();
builder.Services.AddSingleton<IRelayPublisher, NullRelayPublisher>();
builder.Services.AddSingleton<ISessionRelay, InMemorySessionRelay>();
builder.Services.AddSingleton<ISequenceValidator, SessionSequenceValidator>();
builder.Services.AddSingleton<ISessionOrchestrator, InMemorySessionOrchestrator>();

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
            SequenceValidationStatus.Accepted or SequenceValidationStatus.Duplicate => Results.Ok(validation),
            SequenceValidationStatus.GapDetected or SequenceValidationStatus.StaleGeneration or SequenceValidationStatus.FutureGeneration => Results.Conflict(validation),
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
