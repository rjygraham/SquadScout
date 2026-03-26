# SquadScout

A local .NET broker that spawns and orchestrates GitHub Copilot, manages session I/O via PTY, and serves APIs for local and cloud-connected clients.

## Quick Links

- **New to the Broker?** Start with [Quick Start](./docs/QUICK_START.md)
- **Deep Dive:** See [Broker Setup & Configuration](./docs/BROKER_SETUP.md)
- **Run Standalone:** `cd src/SquadScout.Broker && dotnet run`
- **Run Full Stack:** `cd src/SquadScout.AppHost && dotnet run`

## Project Structure

```
src/
  SquadScout.Broker/          → Local HTTP broker, PTY host, session orchestration
  SquadScout.AppHost/         → Aspire orchestration (broker + functions + app)
  SquadScout.App/             → MAUI desktop client
  SquadScout.Functions/       → Azure Functions (negotiation, auth)
  SquadScout.Contracts/       → Shared message contracts and types
  SquadScout.ServiceDefaults/ → Aspire service defaults
tests/
  *.Tests/                    → Unit and integration tests
docs/
  QUICK_START.md              → 30-second setup
  BROKER_SETUP.md             → Full broker configuration and troubleshooting
```

## Getting Started

### Prerequisites

- .NET 10 SDK
- Copilot CLI (`copilot --version`)
- Windows 10+ (POSIX support coming later)

### Build

```bash
dotnet build SquadScout.slnx
```

### Test

```bash
dotnet test SquadScout.slnx
```

### Run

**Broker only (standalone):**
```bash
cd src/SquadScout.Broker
dotnet run
# Launch settings bind http://localhost:5050
# Use `dotnet run --no-launch-profile` to fall back to Broker:ListenUrl (http://127.0.0.1:5071)
```

**Full stack (Aspire orchestration):**
```bash
cd src/SquadScout.AppHost
dotnet run
# AppHost UI: http://localhost:15284
# HTTPS launch-settings URL: https://localhost:17090
```

## Documentation

- [Quick Start](./docs/QUICK_START.md) — Get the broker running in 30 seconds.
- [Broker Setup & Configuration](./docs/BROKER_SETUP.md) — Full configuration guide, endpoints, troubleshooting.
- [.squad/decisions.md](./.squad/decisions.md) — Architectural decisions and team direction.
- [.squad/agents/link/history.md](./.squad/agents/link/history.md) — Broker development context and learnings.

## API Overview

### Health Check

```http
GET /health
GET /alive                        # Development only
```

### Projects

```http
POST /api/projects                # Register project
GET /api/projects                 # List projects
```

### Sessions

```http
POST /api/sessions                # Start session
GET /api/sessions/{sessionId}     # Get session status
POST /api/sessions/{sessionId}/stop   # Stop session
POST /api/sessions/{sessionId}/input  # Send input to session
GET /api/sessions/{sessionId}/telemetry  # Get session diagnostics (Phase 1)
```

## Configuration

All broker settings are environment-driven. Key settings:

| Setting | Default | Purpose |
|---------|---------|---------|
| `Broker:ListenUrl` | `http://127.0.0.1:5071` | Fallback HTTP bind address when launch settings or AppHost do not inject `ASPNETCORE_URLS` |
| `CopilotPty:ExecutablePath` | `copilot` | Copilot CLI path |
| `AzureWebPubSub:ConnectionString` | (empty) | Web PubSub relay (optional) |

Override at runtime:
```bash
dotnet run --no-launch-profile -- --Broker:ListenUrl="http://localhost:5072"
```

See [Broker Setup](./docs/BROKER_SETUP.md) for full configuration reference.

## Architecture

- **Broker Host:** Local HTTP server exposing REST APIs.
- **PTY Host:** Windows ConPTY wrapper for Copilot process lifecycle.
- **Session Orchestration:** In-memory session state and message routing.
- **Relay Integration:** Optional Azure Web PubSub for remote client messaging (publish-only in Phase 1).
- **Orleans (Phase 2):** Grain-based state distribution (currently disabled).

## Known Limitations (Phase 1)

- **No Persistent Registry:** Projects lost on broker restart.
- **Single Session Per Project:** Multi-concurrent sessions deferred to Phase 2.
- **Windows Only:** POSIX TTY support coming later.
- **In-Memory Relay:** Without Web PubSub, no remote client routing.
- **No Graceful Shutdown:** Basic Ctrl+C shutdown only.

## Contributing

Team workstreams and issue ownership defined in [.squad/decisions.md](./.squad/decisions.md).

Broker development tracked under `squad:link` label in GitHub Issues.

## License

(TBD)
