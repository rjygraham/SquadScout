# Quick Start: Running the Broker

New to SquadScout Broker? Start here.

## 30-Second Setup

### Standalone (Broker Only)

```bash
cd src/SquadScout.Broker
dotnet run
```

Broker is live at `http://127.0.0.1:5071`.

### Full Stack (Broker + Functions + MAUI)

```bash
cd src/SquadScout.AppHost
dotnet run
```

All services start; dashboard at `http://localhost:18888`.

## First Steps

> Mobile clients use Azure Web PubSub for broker control, live input, replay recovery, and output streaming. The REST endpoints below remain for local admin/testing; the Azure Function only handles authentication and token negotiation.

### 1. Verify Broker is Running

```bash
curl http://127.0.0.1:5071/health
# Expected: 200 OK
```

### 2. Register a Project

```bash
curl -X POST http://127.0.0.1:5071/api/projects \
  -H "Content-Type: application/json" \
  -d '{
    "projectId": "my-project",
    "displayName": "My Project",
    "repositoryRoot": "."
  }'
```

### 3. List Projects

```bash
curl http://127.0.0.1:5071/api/projects
```

> If you launch with `Orleans__Enabled=true`, project registrations and session replay metadata persist in the broker's Orleans SQLite store. Live PTY sessions are still broker-owned, so restart active sessions after switching Orleans mode.

## Troubleshooting Quick Fixes

| Problem | Fix |
|---------|-----|
| Port 5071 in use | `taskkill /PID <PID> /F` or use `--Broker:ListenUrl="http://127.0.0.1:5072"` |
| `copilot` not found | Install Copilot CLI or set `--CopilotPty:ExecutablePath="/path/to/copilot"` |
| Aspire dashboard won't load | Check that `dotnet run` started successfully; dashboard is at `http://localhost:18888` |

## Full Configuration Guide

See **[BROKER_SETUP.md](./BROKER_SETUP.md)** for detailed configuration, environment overrides, Web PubSub setup, and troubleshooting.

## What's Next?

- Review session lifecycle in the broker [Sessions guide](../src/SquadScout.Broker/Sessions/README.md) (if available).
- Enable Azure Web PubSub for remote mobile routing and broker control messaging.
- Run tests: `dotnet test SquadScout.slnx`.
