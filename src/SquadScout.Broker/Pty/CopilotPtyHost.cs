using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pty.Net;
using SquadScout.Broker.Configuration;

namespace SquadScout.Broker.Pty;

public sealed class CopilotPtyHost : IPtyHost
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CopilotPtyHost> _logger;
    private readonly CopilotPtyHostOptions _options;

    public CopilotPtyHost(
        IOptions<CopilotPtyHostOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<CopilotPtyHost> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value ?? throw new ArgumentException("Copilot PTY options are required.", nameof(options));
    }

    public async Task<IPtySession> StartSessionAsync(PtySessionStartRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            throw new ArgumentException("A session id is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            throw new ArgumentException("A project id is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(_options.ExecutablePath))
        {
            throw new InvalidOperationException("Copilot PTY executable path is not configured.");
        }

        var executablePath = _options.ExecutablePath;
        var workingDirectory = ResolveWorkingDirectory(request);
        var spawnOptions = new PtyOptions
        {
            App = executablePath,
            Cwd = workingDirectory,
            Cols = Math.Max(1, _options.InitialColumns),
            Rows = Math.Max(1, _options.InitialRows),
            CommandLine = [.. _options.BaseArguments, .. request.Arguments],
            Environment = CreateEnvironment(request)
        };

        try
        {
            _logger.LogInformation(
                "Starting PTY session {SessionId} for project {ProjectId} with '{ExecutablePath}' in '{WorkingDirectory}'.",
                request.SessionId,
                request.ProjectId,
                executablePath,
                workingDirectory);

            var connection = await PtyProvider.SpawnAsync(spawnOptions, cancellationToken).ConfigureAwait(false);
            return new CopilotPtySession(
                request,
                connection,
                Math.Max(1, _options.OutputBufferSize),
                Math.Max(1, _options.MaxInputCharactersPerWrite),
                _loggerFactory.CreateLogger<CopilotPtySession>());
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Cancelled PTY startup for session {SessionId} before Copilot launched.",
                request.SessionId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to start PTY session {SessionId} for project {ProjectId}.",
                request.SessionId,
                request.ProjectId);

            throw new PtySessionStartException(
                request.SessionId,
                request.ProjectId,
                executablePath,
                workingDirectory,
                ex);
        }
    }

    private string ResolveWorkingDirectory(PtySessionStartRequest request)
    {
        string workingDirectory;
        string source;

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            workingDirectory = request.WorkingDirectory;
            source = "project repository root";
        }
        else if (!string.IsNullOrWhiteSpace(_options.WorkingDirectory))
        {
            workingDirectory = _options.WorkingDirectory;
            source = "broker default configuration";
        }
        else
        {
            workingDirectory = Environment.CurrentDirectory;
            source = "current broker process directory";
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"The PTY working directory '{workingDirectory}' does not exist.");
        }

        var resolvedPath = Path.GetFullPath(workingDirectory);
        _logger.LogInformation(
            "Using working directory from {Source}: {WorkingDirectory}",
            source,
            resolvedPath);

        return resolvedPath;
    }

    private Dictionary<string, string> CreateEnvironment(PtySessionStartRequest request)
    {
        var environment = new Dictionary<string, string>(_options.Environment, StringComparer.OrdinalIgnoreCase)
        {
            ["SQUADSCOUT_PROJECT_ID"] = request.ProjectId,
            ["SQUADSCOUT_SESSION_ID"] = request.SessionId
        };

        return environment;
    }
}
