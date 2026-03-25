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
# Now listening on http://127.0.0.1:5071
```

**Full stack (Aspire orchestration):**
```bash
cd src/SquadScout.AppHost
dotnet run
# Aspire dashboard: http://localhost:18888
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
GET /alive
```

### Projects

```http
POST /projects                    # Register project
GET /projects                     # List projects
GET /projects/{projectId}         # Get project details
```

### Sessions

```http
POST /sessions/{projectId}/start  # Start session
POST /sessions/{projectId}/stop   # Stop session
POST /sessions/{projectId}/input  # Send input to session
GET /sessions/{projectId}         # Get session status
```

### WebSocket

```http
GET /ws/sessions/{sessionId}      # Subscribe to session messages
```

## Configuration

All broker settings are environment-driven. Key settings:

| Setting | Default | Purpose |
|---------|---------|---------|
| `Broker:ListenUrl` | `http://127.0.0.1:5071` | HTTP server bind address |
| `CopilotPty:ExecutablePath` | `copilot` | Copilot CLI path |
| `AzureWebPubSub:ConnectionString` | (empty) | Web PubSub relay (optional) |

Override at runtime:
```bash
dotnet run --Broker:ListenUrl="http://localhost:5072"
```

See [Broker Setup](./docs/BROKER_SETUP.md) for full configuration reference.

## Architecture

- **Broker Host:** Local HTTP server exposing REST + WebSocket APIs.
- **PTY Host:** Windows ConPTY wrapper for Copilot process lifecycle.
- **Session Orchestration:** In-memory session state and message routing.
- **Relay Integration:** Optional Azure Web PubSub for remote client messaging.
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
