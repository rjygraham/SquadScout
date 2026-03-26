# Quick Start: Running the Broker

New to SquadScout Broker? Start here.

## 30-Second Setup

### Standalone (Broker Only)

```bash
cd src/SquadScout.Broker
dotnet run
```

`dotnet run` uses the broker launch settings, so the standalone broker is live at `http://localhost:5050`.

### Full Stack (Broker + Functions + MAUI)

```bash
cd src/SquadScout.AppHost
dotnet run
```

The AppHost UI starts on `http://localhost:15284` (`https://localhost:17090` for the HTTPS profile).

## First Steps

### 1. Verify Broker is Running

```bash
curl http://localhost:5050/health
# Expected: 200 OK
```

### 2. Register a Project

```bash
curl -X POST http://localhost:5050/api/projects \
  -H "Content-Type: application/json" \
  -d '{
    "projectId": "my-project",
    "displayName": "My Project",
    "repositoryRoot": "."
  }'
```

### 3. List Projects

```bash
curl http://localhost:5050/api/projects
```

## Troubleshooting Quick Fixes

| Problem | Fix |
|---------|-----|
| Port 5050 in use | `taskkill /PID <PID> /F` or run `dotnet run --no-launch-profile -- --Broker:ListenUrl="http://127.0.0.1:5072"` |
| `copilot` not found | Install Copilot CLI or start with `dotnet run -- --CopilotPty:ExecutablePath="/path/to/copilot"` |
| AppHost UI won't load | Check that `dotnet run` started successfully; launch-settings URLs are `http://localhost:15284` and `https://localhost:17090` |

## Full Configuration Guide

See **[BROKER_SETUP.md](./BROKER_SETUP.md)** for detailed configuration, environment overrides, Web PubSub setup, and troubleshooting.

## What's Next?

- Review session lifecycle in the broker [Sessions guide](../src/SquadScout.Broker/Sessions/README.md) (if available).
- Enable Azure Web PubSub for remote client routing.
- Run focused broker tests: `dotnet test .\tests\SquadScout.Broker.Tests\SquadScout.Broker.Tests.csproj`.
