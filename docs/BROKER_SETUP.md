# Local Broker Setup and Configuration

## Overview

The SquadScout Broker is a local .NET host that manages Copilot process I/O via a PTY (pseudo-terminal) interface, orchestrates sessions, and exposes REST APIs for local and remote clients. This guide covers contributor-facing setup and configuration for local development.

## Prerequisites

- **.NET 10 SDK** (or `net10.0` runtime)
- **Copilot CLI** installed and available on PATH (for PTY mode)
  - Verify: `copilot --version`
  - If not found, update your PATH or pass `ExecutablePath` in configuration
- **Windows 10+** (broker uses Windows ConPTY; POSIX TTY support deferred to future phases)

## Broker Modes

The broker can run in two configurations:

### 1. Standalone Mode (Local Development)

Run the broker directly for isolated testing and debugging:

```bash
cd src/SquadScout.Broker
dotnet run
```

**Current launch behavior:**
- **Standalone `dotnet run`:** `launchSettings.json` selects `http://localhost:5050`
- **Config fallback (`appsettings.json`):** `Broker:ListenUrl = http://127.0.0.1:5071` when launch settings are bypassed (for example `dotnet run --no-launch-profile`)
- **Project Registry:** `.squadscout/projects.json` (in-memory, not persisted)
- **Copilot Executable:** `copilot` (from PATH)
- **PTY Buffer:** 1024 characters; 30 rows × 120 columns
- **Azure Web PubSub:** Disabled (empty ConnectionString)

### 2. AppHost Mode (Full Stack)

Run broker alongside Functions and MAUI app using Aspire orchestration:

```bash
cd src/SquadScout.AppHost
dotnet run
```

This launches:
- **AppHost UI** at `http://localhost:15284` (`https://localhost:17090` for the HTTPS profile)
- **Broker** at `http://localhost:5071`
- **Azure Functions** referencing broker
- **MAUI App** (Windows) with broker connection

Watch the AppHost console if you need the exact local URLs echoed at startup.

## Configuration

All broker settings are **environment-driven** via `appsettings.json` and overridable at runtime.

### Core Settings (Broker Section)

**File:** `src/SquadScout.Broker/appsettings.json`

```json
{
  "Broker": {
    "ListenUrl": "http://127.0.0.1:5071",
    "ProjectRegistryPath": ".squadscout\\projects.json"
  }
}
```

| Setting | Type | Default | Notes |
|---------|------|---------|-------|
| `ListenUrl` | string | `http://127.0.0.1:5071` | Fallback HTTP bind address. `dotnet run` normally uses `launchSettings.json` (`http://localhost:5050`), and AppHost injects its own broker endpoint through `ASPNETCORE_URLS`. |
| `ProjectRegistryPath` | string | `.squadscout\projects.json` | Relative path for persisting registered projects. Not yet implemented; in-memory catalog used. |

**Override at runtime:**

```bash
dotnet run --no-launch-profile -- --Broker:ListenUrl="http://0.0.0.0:5071"
```

### Copilot PTY Configuration

**File:** `src/SquadScout.Broker/appsettings.json`

```json
{
  "CopilotPty": {
    "ExecutablePath": "copilot",
    "InitialRows": 30,
    "InitialColumns": 120,
    "OutputBufferSize": 1024,
    "MaxInputCharactersPerWrite": 4096,
    "WorkingDirectory": "",
    "BaseArguments": [],
    "Environment": {}
  }
}
```

| Setting | Type | Default | Notes |
|---------|------|---------|-------|
| `ExecutablePath` | string | `copilot` | Path to Copilot CLI binary. Resolved from PATH if relative. |
| `InitialRows` | int | 30 | PTY initial height (lines). Adjust for content or test scenarios. |
| `InitialColumns` | int | 120 | PTY initial width (characters). Affects line wrapping. |
| `OutputBufferSize` | int | 1024 | Maximum characters buffered per PTY read. Increase if commands produce large output. |
| `MaxInputCharactersPerWrite` | int | 4096 | Maximum input characters allowed per client command. Prevents buffer overflow; reject commands exceeding this. |
| `WorkingDirectory` | string | `""` (current) | Directory in which Copilot process spawns. Empty = resolved to repository root of the registered project at session start. |
| `BaseArguments` | string[] | `[]` | Arguments passed to `copilot` at spawn (e.g., `["--config", "path/to/config"]`). |
| `Environment` | object | `{}` | Environment variables merged into Copilot process. E.g., `{"COPILOT_LOG": "debug"}`. |

**Override at runtime:**

```bash
dotnet run -- \
  --CopilotPty:ExecutablePath="/custom/path/to/copilot" \
  --CopilotPty:InitialRows=50 \
  --CopilotPty:InitialColumns=160
```

### Azure Web PubSub (Optional)

**File:** `src/SquadScout.Broker/appsettings.json`

```json
{
  "AzureWebPubSub": {
    "Hub": "squadscout",
    "ConnectionString": ""
  }
}
```

| Setting | Type | Default | Notes |
|---------|------|---------|-------|
| `Hub` | string | `squadscout` | Web PubSub hub name. Must match negotiation endpoint. |
| `ConnectionString` | string | `""` (empty) | Azure Web PubSub connection string. If empty, relay is disabled (NullRelayPublisher used). |

**To enable Azure Web PubSub:**

1. Create a Web PubSub resource in Azure.
2. Copy the connection string from the Azure Portal (Keys section).
3. Set via environment or config:

   ```bash
   dotnet run -- --AzureWebPubSub:ConnectionString="<your-connection-string>"
   ```

   Or in `appsettings.Development.json`:

   ```json
   {
     "AzureWebPubSub": {
       "ConnectionString": "Endpoint=https://...; AccessKey=..."
     }
   }
   ```

**Without Web PubSub:**
- Broker runs in **standalone mode**.
- Session messages are buffered **in-memory** only (Phase 1 telemetry buffer).
- No publish to remote clients (NullRelayPublisher used).
- Suitable for local testing and debugging.

### Orleans (Phase 2 — Currently Disabled)

```json
{
  "Orleans": {
    "Enabled": false
  }
}
```

Orleans-based session grain distribution is not yet implemented. Leave `Enabled: false` for Phase 1.

## Endpoints

### Health Check

```http
GET /health
GET /alive  # Development environment only
```

Returns `200 OK` if broker is running. Used by Aspire and load balancers.

### Project Management

- **Register Project:** `POST /api/projects`
- **List Projects:** `GET /api/projects`

**Current behavior:** No GET by projectId endpoint exists. Projects are retrieved from the list endpoint.

See `src/SquadScout.Broker/Program.cs` for endpoint definitions.

### Session APIs

- **Start Session:** `POST /api/sessions`
  - Request body: `StartSessionCommand` with `ProjectId`, `RequestedBy`, and optional `Arguments`
  - Returns: `202 Accepted` with session details
  
- **Get Session Status:** `GET /api/sessions/{sessionId}`
  - Returns: Session state and metadata
  
- **Stop Session:** `POST /api/sessions/{sessionId}/stop`
  - Request body: `StopSessionCommand` with `ProjectId`, `RequestedBy`, and optional `Reason`
  - Returns: Final session state
  
- **Send Input:** `POST /api/sessions/{sessionId}/input`
  - Request body: `MessageEnvelope<InputChunkPayload>` with sequence metadata
  - Returns: Sequence validation result (Accepted/Duplicate/GapDetected/Conflict)
  
- **Get Session Telemetry:** `GET /api/sessions/{sessionId}/telemetry`
  - Returns: Diagnostic snapshot including sequence state and message buffer (Phase 1)

See `src/SquadScout.Broker/Program.cs` for endpoint implementations.

### WebSocket Support (Phase 2)

**Phase 1 Status:** No WebSocket endpoints are implemented. Session output routing currently uses Azure Web PubSub publish for remote clients. Local clients poll session state or subscribe via Web PubSub client connections. Direct broker WebSocket support is deferred to Phase 2.

## Project Registration

Projects are registered via REST API. Each project must have:

- **ProjectId:** Unique identifier (e.g., `my-repo-1`)
- **DisplayName:** Human-readable name
- **RepositoryRoot:** Path to the repository root where Copilot executes

**Example:**

```bash
curl -X POST http://localhost:5050/api/projects \
  -H "Content-Type: application/json" \
  -d '{
    "projectId": "squadscout-repo",
    "displayName": "SquadScout Main Repo",
    "repositoryRoot": "D:\\GitHub\\SquadScout"
  }'
```

**Current behavior:**
- Projects stored **in-memory** (lost on broker restart).
- `ProjectRegistryPath` not yet implemented; Phase 2 will add persistent storage.

## Local Development Workflow

### 1. Build and Test

```bash
cd D:\GitHub\SquadScout
dotnet build SquadScout.slnx
dotnet test .\tests\SquadScout.Broker.Tests\SquadScout.Broker.Tests.csproj
```

### 2. Run Broker Standalone

```bash
cd src/SquadScout.Broker
dotnet run
```

Expected output:
```
info: SquadScout.Broker[0]
      Now listening on: http://localhost:5050
```

### 3. Test Endpoints

```bash
# Health check
curl http://localhost:5050/health

# Register a project
curl -X POST http://localhost:5050/api/projects \
  -H "Content-Type: application/json" \
  -d '{"projectId": "test", "displayName": "Test Project", "repositoryRoot": "."}'

# List projects
curl http://localhost:5050/api/projects
```

### 4. Run Full Stack (Aspire)

```bash
cd src/SquadScout.AppHost
dotnet run
```

Open `http://localhost:15284` (or `https://localhost:17090`) to see all services running.

## Troubleshooting

### Broker fails to start

**Error:** `The address is already in use.`

- **Cause:** Port 5050 is occupied when using the default launch profile.
- **Solution:** Kill the process using the port or change `ListenUrl` in config.

```bash
# Find and kill process on port 5050 (Windows)
netstat -ano | findstr :5050
taskkill /PID <PID> /F
```

### Copilot not found

**Error:** `Unable to find copilot executable. Path: copilot`

- **Cause:** Copilot CLI not installed or not on PATH.
- **Solution:** Install Copilot CLI or specify full path in config.

```bash
# Verify Copilot is installed
copilot --version

# If not on PATH, set ExecutablePath to full path
dotnet run -- --CopilotPty:ExecutablePath="C:\Program Files\GitHub\Copilot\copilot.exe"
```

### PTY buffer overflow

**Error:** Session closes or input is rejected unexpectedly.

- **Cause:** Input exceeds `MaxInputCharactersPerWrite` (default 4096).
- **Solution:** Increase limit in config or send smaller chunks.

```bash
dotnet run -- --CopilotPty:MaxInputCharactersPerWrite=8192
```

### Session starts but no output

**Cause:** Output buffering misconfiguration.

- **Workaround:** Increase `OutputBufferSize` and/or `InitialRows` to capture more terminal state.

```bash
dotnet run -- \
  --CopilotPty:OutputBufferSize=2048 \
  --CopilotPty:InitialRows=50
```

### Web PubSub connection fails

**Error:** Broker starts but relay is unresponsive.

- **Cause:** Invalid or missing Web PubSub connection string.
- **Solution:** Verify connection string in Azure Portal, or disable relay for local testing.

```bash
# Disable relay (standalone mode)
dotnet run -- --AzureWebPubSub:ConnectionString=""
```

## Standalone vs. Full Stack

| Aspect | Standalone | AppHost (Full Stack) |
|--------|------------|---------------------|
| **Launch** | `cd src/SquadScout.Broker && dotnet run` | `cd src/SquadScout.AppHost && dotnet run` |
| **Scope** | Broker only | Broker + Functions + MAUI |
| **Debugging** | Single process, easy breakpoints | Aspire orchestrates multiple; dashboard helps |
| **Dependencies** | Only .NET + Copilot | .NET + Copilot + Azure CLI (optional) |
| **Use Case** | Rapid broker iteration, API testing | End-to-end flow testing, cross-process messaging |

## Environment Overrides

All `appsettings.json` settings can be overridden via environment variables by replacing `:` with `__` in the variable name:

```powershell
# Example: set Broker ListenUrl and Copilot executable
$env:Broker__ListenUrl = "http://localhost:5072"
$env:CopilotPty__ExecutablePath = "C:\copilot.exe"
$env:AzureWebPubSub__ConnectionString = "Endpoint=..."

dotnet run
```

Or pass application arguments directly:

```bash
dotnet run --no-launch-profile -- --Broker:ListenUrl="http://localhost:5072"
```

## Known Limitations (Phase 1)

- **No Persistent Project Registry:** Projects are lost when broker restarts. Use registration API or AppHost to re-register.
- **Single Session Per Project:** Multi-concurrent sessions per project not supported; deferred to Phase 2.
- **In-Memory Relay Only:** Without Web PubSub, session data is not routed to remote clients.
- **Windows ConPTY Only:** POSIX TTY (Linux/macOS) support deferred.
- **No Graceful Shutdown:** Ctrl+C stops immediately; process cleanup is basic. Full shutdown flow planned for Phase 3.
- **Orleans Disabled:** Grain-based orchestration not ready; in-memory session state used.

## Next Steps

- **Phase 2:** Persistent project registry, multi-session support, Orleans integration.
- **Phase 3:** Graceful shutdown, advanced observability, POSIX TTY support.
- **Phase 4:** Windows Service / systemd deployment, advanced scaling.

## Additional Resources

- **Broker Code:** `src/SquadScout.Broker/`
- **Configuration Classes:** `src/SquadScout.Broker/Configuration/`
- **Session Orchestration:** `src/SquadScout.Broker/Sessions/`
- **PTY Host:** `src/SquadScout.Broker/Pty/`
- **Project Catalog:** `src/SquadScout.Broker/Projects/`

## Questions or Issues?

Refer to:
- `.squad/decisions.md` for architectural decisions
- GitHub Issues (labeled `squad:link`) for active work
- `.squad/agents/link/history.md` for session context and learnings
