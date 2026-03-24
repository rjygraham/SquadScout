---
name: "dotnet-phase-scaffolding"
description: "Patterns for creating a buildable .NET solution skeleton across broker, MAUI, Functions, and tests"
domain: "architecture"
confidence: "medium"
source: "earned"
---

## Context

Use this skill when a repo needs a first-pass .NET solution skeleton that spans multiple app types with different runtime constraints, but later backlog items still need room to implement real behavior.

## Patterns

- **Separate layout early:** Put runtime projects in `src\` and validation projects in `tests\`, then make the solution entrypoint explicit (`.sln` or `.slnx`) so later automation has one canonical target.
- **Multi-target only the shared contract layer:** When one app type lags framework support (for example Azure Functions on `net8.0` while the broker and MAUI app move to `net10.0`), multi-target the contract project instead of forcing every project onto the lowest common denominator.
- **Keep scaffold behavior in-memory:** For phase-1 skeleton work, create interfaces and no-op or in-memory implementations for relay, persistence, and orchestration seams. That preserves compile-time integration without pre-solving later runtime issues.
- **Gate MAUI targets by host OS:** Only include iOS and Mac Catalyst targets on macOS, and Windows targets on Windows. This keeps the shared solution buildable from the active workstation.
- **Pin the SDK when preview tooling is required:** If the scaffold depends on preview MAUI or .NET features, add `global.json` so the whole team resolves the same toolchain.

## Examples

- `src\SquadScout.Contracts` multi-targets `net8.0;net10.0` while `src\SquadScout.Functions` stays `net8.0`.
- `src\SquadScout.Broker` exposes localhost-only stub endpoints and registers `InMemoryProjectCatalog`, `InMemorySessionOrchestrator`, and `NullRelayPublisher`.
- `src\SquadScout.App\SquadScout.App.csproj` adds `net10.0-ios` and `net10.0-maccatalyst` only on macOS.

## Anti-Patterns

- Do NOT force Azure Functions, MAUI, and broker projects onto one target framework if one workload is not actually ready for it.
- Do NOT implement cloud relay or Orleans behavior in the scaffold issue just because interfaces are needed now.
- Do NOT leave MAUI target frameworks unconditional across OSes; that turns the scaffold into a workstation-specific build break.
