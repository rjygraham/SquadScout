# Seraph: Aspire ServiceDefaults decision

## Context
- SquadScout.Functions remains on net8.0 while SquadScout.App and SquadScout.Broker target net10.0.
- We wanted one reviewable Aspire introduction that helps telemetry/logging work without disrupting current broker and MAUI behavior.

## Decision
- Use a custom shared src\SquadScout.ServiceDefaults project that multi-targets net8.0;net10.0 instead of the stock ASP.NET Core-only ServiceDefaults shape.
- Keep broker health behavior on its existing explicit /health endpoint instead of switching to Aspire health endpoint mapping.
- Let Aspire AppHost own broker endpoint orchestration when it sets ASPNETCORE_URLS; otherwise the broker keeps using Broker:ListenUrl.

## Why
- The stock Aspire ServiceDefaults template assumes ASP.NET Core health-check wiring, but the MAUI app needs the MAUI OpenTelemetry initializer pattern and the Functions worker needs a net8.0-compatible shared project.
- This keeps one shared defaults project for logging, OTEL, resilience, and service discovery while minimizing behavioral changes in existing endpoints and configs.

## Follow-up
- If issue #31 grows into end-to-end trace correlation work, layer broker-specific incoming-request instrumentation and Azure Monitor export settings on top of this shared foundation.
