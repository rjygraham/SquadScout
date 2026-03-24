# Squad Team

## Project Context

- Requested by: Ryan Graham
- Product: A PTY-style wrapper for GitHub Copilot that can be remotely operated from a mobile app.
- Local runtime: A local .NET broker host process spawns the Copilot instance and forwards messages to and from it.
- Mobile client: A .NET MAUI app targeting .NET 10.
- Realtime channel: Azure Web PubSub between the broker and the MAUI app.
- Authentication: Microsoft Entra for the MAUI app, with an Azure Function using managed identity to issue Web PubSub tokens.
- Local UX: The broker should expose a simple local web UI for registering multiple projects by local repo path and persisting that configuration.
- State option under evaluation: Co-host Microsoft Orleans in the broker for state storage and replayable message flow across reconnects.
- Initial user goal: Support session start from the MAUI app by issuing a command.

## Members

| Name | Role | Domain | Badge |
| --- | --- | --- | --- |
| Neo | Lead | Architecture, decomposition, review | 🏗️ |
| Trinity | Mobile Dev | .NET MAUI app, session UX, command flows | ⚛️ |
| Link | Broker Dev | Local .NET broker, PTY wrapper, process I/O, web UI | 🔧 |
| Seraph | Cloud/Auth Dev | Azure Web PubSub, Azure Function, Entra auth | 🔒 |
| Morpheus | Security & Performance | Threat modeling, replay safety, latency, resilience | 🔒 |
| Switch | Tester | Test strategy, reconnect behavior, regression coverage | 🧪 |
| Scribe | Session Logger | Decisions, logs, cross-agent context sharing | 📋 |
| Ralph | Work Monitor | Backlog scans, issue flow, keep-alive | 🔄 |

