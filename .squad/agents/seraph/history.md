# Seraph History

## Core Context (Summarized)

**Domain:** Cloud/Auth for SquadScout (Azure Web PubSub relay + Function token broker + Entra auth)  
**6 Workstreams:** (1) Entra auth flow, (2) Function integration, (3) PubSub routing, (4) token refresh/reconnect, (5) multi-broker affinity, (6) observability. WS-1/WS-2 unblock rest.

**Key Technical Decisions:**
- Token minting via Function managed identity (no connection strings in code)
- Session groups: `session:{projectId}:{sessionId}[:brokerId]`
- App-level reliability via Orleans (single-threaded turns, sequence numbers, replay buffer)
- No distributed session cache Phase 1/2; broker is source of truth
- Token TTL 1 hour; proactive refresh at 50-min mark
- Web PubSub transport is best-effort; app handles recovery

**Cross-Agent Dependencies:** Link (Entra), Trinity (MAUI shell), Morpheus (infra), Neo (grains), Switch (review)

**User Constraints:** Single-user + multi-machine; offline-first local broker fallback; MAUI chat UI + text-to-speech.

## Day 1 Context

- User: Ryan Graham
- Project: Mobile clients connect through Azure Web PubSub to a local Copilot broker.
- Stack: Microsoft Entra, Azure Function with managed identity, Azure Web PubSub, and a .NET-based local broker.
- Key concerns: secure token issuance, authenticated mobile access, and reconnect-safe cloud contracts.

## Learnings

### 2026-03-24: Cloud/Auth Domain Decomposition (Archived in Core Context)

### 2026-03-25: Merge Watcher — PR #41 & #42 Monitoring

**Status:** PRs #41 & #42 both merged to main. WS-2 (Function integration) complete, WS-3 (PubSub routing) unblocked.

- **PR #42:** Issue #8 safety baseline (merged 2026-03-24T22:28:52Z)
- **PR #41:** Azure Function negotiate endpoint (merged 2026-03-24T18:30:09Z), key artifact `NegotiateFunction.cs` (Easy Auth boundary, managed-identity token minting)
- **Issue #7:** Auto-closed by PR #41 body

### Issue #9 Handoff — Trinity Implementation Complete (2026-03-24T23:39:21Z)

MAUI App Shell complete (cross-platform, chat UI, 36 tests passing). PR #43 in review (Switch gate). Unblocks WS-3/WS-4 client pairing with broker relay and token refresh flows.

### Issue #13 / PR #44 Revision — Stop Failure Gate Hardening (2026-03-25)

- **Failure-path invariant tightened:** `src\SquadScout.Broker\Relay\InMemorySessionRelay.cs` now reacquires `StopInputGate` before clearing `_stopRequested` after a `TerminateAsync()` failure, so stop-failure recovery stays serialized with input admission instead of reopening the accepted-stop race mid-recovery.
- **Deterministic proof extended:** `tests\SquadScout.Broker.Tests\SessionRelayPipelineTests.cs` now covers both the successful stop overlap and a failing-stop overlap, using the gateable PTY harness plus the relay's shared stop gate to prove `session_stop_failed` stays blocked behind recovery before input can resume.
- **Validation:** In `D:\GitHub\SquadScout-13`, `dotnet build .\SquadScout.slnx -nologo`, `dotnet test .\tests\SquadScout.Broker.Tests\SquadScout.Broker.Tests.csproj -nologo --filter "FullyQualifiedName~SessionRelayPipelineTests"`, and `dotnet test .\SquadScout.slnx -nologo --no-build` all passed (61/61 full-suite).

### PR #44 Merge Reconciliation — Dirty → Clean → Merged (2026-03-25)

- **Dirty cause:** PR #44 was behind `main` after #45 and #46 landed, so GitHub reported `mergeable_state: dirty` even though the approved stop/start behavior itself was still valid.
- **Chosen merge path:** Rebased `squad/13-broker-session-start-stop-endpoints` onto current `origin/main`, preserving the stop/input gate hardening while absorbing the new Aspire/observability broker bootstrap changes from #46 without reintroducing the race.
- **Post-reconcile validation:** In `D:\GitHub\SquadScout-13`, `dotnet build .\SquadScout.slnx -nologo`, `dotnet test .\tests\SquadScout.Broker.Tests\SquadScout.Broker.Tests.csproj -nologo --filter "FullyQualifiedName~SessionRelayPipelineTests"`, and `dotnet test .\SquadScout.slnx -nologo --no-build` all passed again (focused broker tests 10/10, full suite 67/67).
- **Merge outcome:** After force-pushing the rebased branch, GitHub reported PR #44 clean; it was then squash-merged to keep `main` history tidy while retaining the PR body so `closes #13` completed as intended.

### PR #47 Merge-Watch Conditional Activation (2026-03-25T01:22:48Z)

- **Event:** Conditional merge-watch standup for PR #47, awaiting Switch review verdict
- **Requested by:** Ryan Graham (parallel coordination with Switch)
- **Trigger:** Switch issues explicit APPROVED verdict
- **Watch scope (on approval):** Verify mergeable state, execute merge, validate post-merge integration
- **Escalation:** Cancel standby if Switch issues REJECTED or BLOCKED verdict
- **Orchestration logs:** 2026-03-25T01-22-48Z-seraph.md
### 2026-03-25: Aspire orchestration + ServiceDefaults groundwork

- Aligned the repo's new Aspire AppHost and ServiceDefaults rollout with Seraph-owned startup concerns after the shared Aspire scaffolding landed on main.
- Documented why the shared defaults project stays multi-targeted for net8.0 and net10.0 so the Azure Functions worker can opt into AddServiceDefaults() while the MAUI app can initialize OpenTelemetry via the MAUI-specific startup hook.
- Preserved broker compatibility by surfacing the effective ASP.NET Core listen URL in the root status payload while still letting standalone runs honor Broker:ListenUrl.
- Updated the MAUI app to create broker clients through an Aspire-configured HttpClient factory seam so resilience and future service discovery can flow into mobile-to-broker calls without breaking offline fallbacks.

### 2026-03-25: Issue #11 — PubSub Client Connection Service

- **Failure-path invariant tightened:** `src\SquadScout.Broker\Relay\InMemorySessionRelay.cs` now reacquires `StopInputGate` before clearing `_stopRequested` after a `TerminateAsync()` failure, so stop-failure recovery stays serialized with input admission instead of reopening the accepted-stop race mid-recovery.
- **Deterministic proof extended:** `tests\SquadScout.Broker.Tests\SessionRelayPipelineTests.cs` now covers both the successful stop overlap and a failing-stop overlap, using the gateable PTY harness plus the relay's shared stop gate to prove `session_stop_failed` stays blocked behind recovery before input can resume.
- **Validation:** In `D:\GitHub\SquadScout-13`, `dotnet build .\SquadScout.slnx -nologo`, `dotnet test .\tests\SquadScout.Broker.Tests\SquadScout.Broker.Tests.csproj -nologo --filter "FullyQualifiedName~SessionRelayPipelineTests"`, and `dotnet test .\SquadScout.slnx -nologo --no-build` all passed (61/61 full-suite).

### PR #44 Merge Reconciliation — Dirty → Clean → Merged (2026-03-25)

- **Dirty cause:** PR #44 was behind `main` after #45 and #46 landed, so GitHub reported `mergeable_state: dirty` even though the approved stop/start behavior itself was still valid.
- **Chosen merge path:** Rebased `squad/13-broker-session-start-stop-endpoints` onto current `origin/main`, preserving the stop/input gate hardening while absorbing the new Aspire/observability broker bootstrap changes from #46 without reintroducing the race.
- **Post-reconcile validation:** In `D:\GitHub\SquadScout-13`, `dotnet build .\SquadScout.slnx -nologo`, `dotnet test .\tests\SquadScout.Broker.Tests\SquadScout.Broker.Tests.csproj -nologo --filter "FullyQualifiedName~SessionRelayPipelineTests"`, and `dotnet test .\SquadScout.slnx -nologo --no-build` all passed again (focused broker tests 10/10, full suite 67/67).
- **Merge outcome:** After force-pushing the rebased branch, GitHub reported PR #44 clean; it was then squash-merged to keep `main` history tidy while retaining the PR body so `closes #13` completed as intended.

### PR #47 Merge-Watch Conditional Activation (2026-03-25T01:22:48Z)

- **Event:** Conditional merge-watch standup for PR #47, awaiting Switch review verdict
- **Requested by:** Ryan Graham (parallel coordination with Switch)
- **Trigger:** Switch issues explicit APPROVED verdict
- **Watch scope (on approval):** Verify mergeable state, execute merge, validate post-merge integration
- **Escalation:** Cancel standby if Switch issues REJECTED or BLOCKED verdict
- **Orchestration logs:** 2026-03-25T01-22-48Z-seraph.md

### Issue #11 — PubSub Client Connection Service (2026-03-25)

- **Worktree baseline sync:** Rebasing `squad/11-pubsub-client-connection-service` onto `origin/main` was necessary before implementation so the branch inherited merged negotiate hardening (#7/#12), MAUI shell work (#9), and Aspire defaults (#46/#47) instead of the older scaffold-only snapshot.
- **Client transport shape shipped:** `src\SquadScout.App\Services\MessagingConnectionService.cs` now owns negotiate/connect/disconnect/reconnect-attempt flows against Azure Web PubSub using the `json.webpubsub.azure.v1` subprotocol, tracks session-scoped outbound/inbound envelopes, and preserves the negotiated `SessionGroup` contract for later routing work.
- **Local dev bridge:** `src\SquadScout.App\Services\PubSubNegotiationClient.cs` sends localhost development identity headers only when the configured negotiate endpoint is loopback and auth mode is `LocalDevelopment`, so local end-to-end work stays practical without weakening the trusted Easy Auth boundary in cloud mode.
- **UX surface:** `src\SquadScout.App\ViewModels\ActiveSessionViewModel.cs` and `Views\ActiveSessionPage.xaml` now surface live transport state changes continuously and expose an explicit "Retry live transport" flow when the socket faults.
- **Validation:** In `D:\GitHub\SquadScout-11`, `dotnet build .\SquadScout.slnx -nologo`, `dotnet test .\tests\SquadScout.App.Tests\SquadScout.App.Tests.csproj -nologo`, and `dotnet test .\SquadScout.slnx -nologo --no-build` all passed (72/72 full-suite after the new app transport coverage landed).

### Issue #51 — Web PubSub Upstream Authentication Hardening (2026-03-25)

- **Dual validation path locked:** `src\SquadScout.Functions\Upstream\WebPubSubUpstreamAuthenticator.cs` now authenticates upstream POSTs before any event processing by first checking `WebHook-Request-Origin`, then accepting either a trusted Easy Auth principal inside the Functions host boundary or a valid Web PubSub signature computed from the configured service access key(s) and `ce-connectionId`.
- **Header compatibility preserved:** The handler treats Azure's documented `ce-signature` header as canonical but also accepts `WebHook-Signature` as an alias so the security follow-up from PR #50 is closed without relying on ambiguous review terminology.
- **Local/cloud configuration seam:** `src\SquadScout.Functions\Configuration\FunctionsHostOptions.cs` and `local.settings.sample.json` now expose `WebPubSubUpstreamAccessKeys` for local or key-based validation plus `TrustedUpstreamPrincipalIds` for managed-identity/Easy-Auth deployments, keeping cloud auth strict while still practical for local broker work.
- **Regression coverage expanded:** `tests\SquadScout.Broker.Tests\PubSubUpstreamHandlerTests.cs` now proves accepted signed requests, alias-header acceptance, trusted managed-identity acceptance, and rejected missing/invalid/untrusted requests before the broker forwarder can run.
- **Validation:** In `D:\GitHub\SquadScout`, `dotnet test .\tests\SquadScout.Broker.Tests\SquadScout.Broker.Tests.csproj -nologo --filter "FullyQualifiedName~PubSubUpstreamHandlerTests"`, `dotnet build .\SquadScout.slnx -nologo`, and `dotnet test .\SquadScout.slnx -nologo --no-build` all passed (focused upstream tests 10/10, full suite 90/90).
