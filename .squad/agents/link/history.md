# Link History

## Day 1 Context

- User: Ryan Graham
- Project: A local .NET broker spawns and wraps GitHub Copilot for remote operation.
- Stack: .NET broker host, Azure Web PubSub transport, local web UI, persisted project paths, and possible Orleans co-hosting.
- Key concerns: process spawning, PTY-style transport semantics, multi-project setup, and reliable message handoff.

## Learnings

### Workstream Decomposition (2026-03-24)

- **5 sequential workstreams proposed:** Foundation & PTY bridge → Project config → Session lifecycle → Orleans grains (Phase 2) → Observability (Phase 3).
- **First executable slice:** 2 weeks broker + 1.5 weeks MAUI + 1 week cloud = ~4.5 weeks end-to-end for MVP (start session → spawn Copilot → I/O round-trip).
- **Key sequencing constraint:** Message contract (envelope schema, sequence semantics) must be locked first; Switch and Morpheus unblock broker WS-1.
- **In-memory first, Orleans second:** Proves raw PTY ↔ PubSub datapath before grain complexity; migration to Orleans in Phase 2 with feature flag or careful cutover.
- **PTY buffering risk:** Windows ConPTY vs. POSIX TTY behavior asymmetry; early smoke test recommended, don't assume 500-message buffer is universal.
- **Single-session per project MVP:** Multi-concurrent sessions deferred; acceptable for Phase 1 foreground app with one MAUI user.
- **Broker restart = state loss until Phase 2:** In-memory registry is acceptable for development; document as known limitation.
- **Graceful shutdown scope:** Phase 3 covers SIGTERM/Ctrl+C handlers; service-mode deployment (Windows Service / systemd) deferred to Phase 4.

### User Directives Accepted & Ready for Execution (2026-03-24)

- **WS-2: Broker PTY Bridge ownership assigned.** Includes direct Copilot spawn (no shell), mock PTY harness in first executable slice, ConPTY lifecycle management.
- **MudBlazor selected** for project configuration UI (WS-3 local web UI scope).
- **Status:** Scribe consolidated all directives to decisions.md. Team workstreams finalized; ready to proceed with Phase 1 execution.

### 2026-03-24T17:31:17Z — GitHub Issues Backlog Imported

- **Import context:** Neo created GitHub issues #1–#34 in rjygraham/SquadScout with full phase gate preservation and routing labels.
- **Link ownership:** Issues #1 (Solution scaffolding), #6 (Relay pipeline), #13 (Session endpoints), #18 (Orleans host), #20 (Project grain), #25 (Blazor UI), #31 (Logging), #32 (Graceful shutdown).
- **Label pattern:** All issues tagged with `squad` + owner label (e.g., `squad:link`) + phase label (e.g., `phase:1`).
- **Coordination note:** Issue #16 gates Phase 1→2; Issue #24 gates Phase 2→3. Link coordinates with Trinity (MAUI), Morpheus (auth/security), Switch (testing).
- **Status:** All team histories updated. Ready for issue assignment and Phase 1 kickoff.

### Issue #1 Scaffold Delivery (2026-03-24)

- **Solution entrypoint:** `SquadScout.slnx` now owns the Phase 1 skeleton, with runtime projects under `src\` and validation in `tests\`.
- **Shared source contract pattern:** `src\SquadScout.Contracts` is the cross-project contract source and multi-targets `net8.0;net10.0` so Functions can stay isolated-worker compatible while broker, MAUI, and tests move on `net10.0`.
- **Broker seam placement:** `src\SquadScout.Broker` hosts localhost-only stubs for project registration, session orchestration, relay publishing, and config binding; Orleans and Web PubSub stay explicit seams rather than premature implementations.
- **Platform-safe MAUI scaffold:** `src\SquadScout.App` only adds iOS/Mac Catalyst targets on macOS and Windows targets on Windows, which keeps the shared solution buildable from this Windows workstation.
- **Verified validation path:** `dotnet build SquadScout.slnx && dotnet test SquadScout.slnx` succeeds after scaffold creation, giving later work a stable baseline.

### Issue #2 Revision — Message Envelope Reset Safety (2026-03-25)

- **Replay identity locked:** Ordered broker frames now use `{ sessionId, generation, sequence }` as the replay identity, with `Generation` incrementing whenever ordered state resets without minting a new session id.
- **Sequence ownership clarified:** `src\SquadScout.Contracts\Messages\MessageEnvelope.cs` now treats `Sequence` as a broker-owned, replay-only field and adds `ClientSequence` for client-originated dedupe/correlation so client traffic cannot accidentally join the broker replay domain.
- **Reset boundary made explicit:** `src\SquadScout.Contracts\Messages\ReplayResponsePayload.cs` repeats the active replay `Generation`, and acknowledgements reset with generation changes.
- **Wire shape tightened:** `src\SquadScout.Contracts\Messages\SessionMessageSerializer.cs` now omits null optional members so control frames do not emit ambiguous empty sequencing fields.
- **Contract proof path:** `tests\SquadScout.Broker.Tests\MessageEnvelopeContractTests.cs` now covers broker-owned sequencing, client-owned sequencing, and generation mismatch handling for reconnect/replay safety.

### Issue #3 Revision — Missing Failure-Mode Coverage (2026-03-25)

- **Coverage pattern reinforced:** Future-generation drift and trust-boundary mismatch need their own tests even when adjacent stale-generation and session-id paths already pass; they protect different broker failure modes.
- **Replay rejection seam stays explicit:** `src\SquadScout.Broker\Sessions\InMemorySessionOrchestrator.cs` rejects replay envelopes whose `ProjectId` or `SessionId` do not match the targeted runtime session before replay/validation logic runs.
- **Focused validation files:** The regression proof points for this work are `tests\SquadScout.Broker.Tests\SessionSequenceValidatorTests.cs` and `tests\SquadScout.Broker.Tests\InMemorySessionOrchestratorReplayTests.cs`.

### Issue #3 Revision Assignment (2026-03-24T19:30:09Z)

- **Previous owner:** Morpheus (Issue #3 implementation, commit `8068e81`)
- **Revision owner:** Link (assigned by Switch formal review outcome)
- **Reason for reassignment:** Morpheus is locked out after rejection; Link takes next revision cycle
- **Coverage gaps to address:**
  1. Add explicit `FutureGeneration` validation test in `tests\SquadScout.Broker.Tests\SessionSequenceValidatorTests.cs`
  2. Add explicit `ProjectId` mismatch replay rejection test in `tests\SquadScout.Broker.Tests\InMemorySessionOrchestratorReplayTests.cs`
- **Review context:** Switch verified build, focused broker tests (12/12), and full suite (20/20) all pass. No implementation bugs found. Rejection is purely coverage-driven per Switch charter.
- **Next step:** Incorporate coverage gaps and request re-review from Switch or assigned reviewer before proceeding to Phase 2 grain activation.

### Issue #5 CopilotPtyHost Completion (2026-03-25)

- **Real PTY host landed:** `src\SquadScout.Broker\Pty\CopilotPtyHost.cs` now binds direct-spawn Copilot PTY startup behind `IPtyHost`, with config in `src\SquadScout.Broker\Configuration\CopilotPtyHostOptions.cs` and DI/appsettings wiring in `src\SquadScout.Broker\Program.cs` / `src\SquadScout.Broker\appsettings.json`.
- **Event seam preserved:** `CopilotPtySession` stays on the issue #4 contract and emits only `PtySessionEvent.Started`, `Output`, and `Exited`; `src\SquadScout.Broker\Pty\PtySessionEnvelopePump.cs` remains the broker-owned translation point into sequenced envelopes.
- **Critical lifecycle pattern:** Natural process exit must wait for PTY output drain before publishing `Exited`, otherwise final buffered output can be truncated. Forced termination still reports `Exited(null)`, but only when termination was already requested at the moment process exit is observed.
- **Native dependency packaging:** Broker and broker tests both copy the `sch.pty.net` native files after build via `PlatformTarget=x64`, keeping real PTY tests runnable from the solution build output.
- **Proof path:** `tests\SquadScout.Broker.Tests\CopilotPtyHostTests.cs` now covers direct spawn success, startup failure surfacing, pre-start cancellation, idempotent teardown, exit-code reporting, chunked output streaming, and real PTY-to-envelope pumping without introducing shell mode.
- **Validation:** `dotnet build .\SquadScout.slnx -nologo` and `dotnet test .\SquadScout.slnx -nologo --no-build` both pass on this workspace after the PTY lifecycle fix.

### Issue #13 Broker Session Start/Stop Endpoints (2026-03-25)

- **Lifecycle surface added:** `src\SquadScout.Broker\Program.cs` now exposes `POST /api/sessions/{sessionId}/stop`, and the broker contracts add `src\SquadScout.Contracts\Sessions\StopSessionCommand.cs` for caller-supplied project/session context.
- **Start validation tightened:** `src\SquadScout.Broker\Relay\InMemorySessionRelay.cs` now rejects unknown projects and missing/stale repository roots before minting a broker session, so invalid start requests return actionable 404/409 errors instead of creating orphaned pending sessions.
- **Stop sequencing stays single-owner:** Stop requests are handled inside `InMemorySessionRelay`, which validates `{ sessionId, projectId }`, marks stop-in-progress to block new input, terminates the PTY, and then lets the existing `PtySessionEnvelopePump` publish the final replayable `SessionLifecycle(Stopped)` envelope from the PTY `Exited` event.
- **Proof path:** `tests\SquadScout.Broker.Tests\SessionRelayPipelineTests.cs` now covers successful stop, project mismatch, already-stopped rejection, and unknown-project start rejection; `dotnet build .\SquadScout.slnx -nologo` plus `dotnet test .\SquadScout.slnx -nologo --no-build` both pass in `D:\GitHub\SquadScout-13`.

### PR #46 Merge Watch Assignment (2026-03-25T00:42:44Z)

- **Source:** Switch formal review APPROVED verdict on PR #46 (Aspire / ServiceDefaults Revision).
- **Artifact:** PR #46 / branch `squad/31-aspire-service-defaults-revision` / commit `2c20bae`.
- **Validation:** Build ✅ | Tests 55/55 ✅ | AppHost smoke start ✅ | Broker `/health` returned `{"status":"ok"}` ✅.
- **Merge-risk assessment:** Low; local validation green across build, full test suite, and smoke start. GitHub checks absent.
- **Watch state:** Active for PR #46 main branch integration.
- **Monitoring plan:** GitHub PR status for merge/close events, post-merge CI/CD pipeline status, AppHost smoke validation post-merge.
- **Escalation trigger:** AppHost smoke regression post-merge or GitHub check run failures; assess rollback with Switch.


### Issue #5 Completion — CopilotPtyHost Direct Spawn (2026-03-24T21:36:52Z)

- **Mission:** Implement Copilot PTY host using Pty.Net, preserving the PTY seam established in issue #4, with comprehensive failure-mode and lifecycle coverage.
- **Deliverables created:**
  1. `src\SquadScout.Broker\Configuration\CopilotPtyHostOptions.cs` — Dependency injection config for Copilot path and working directory
  2. `src\SquadScout.Broker\Pty\CopilotPtyHost.cs` — Pty.Net-backed host implementing `IPtyHost` contract
  3. `src\SquadScout.Broker\Pty\CopilotPtySession.cs` — Session lifecycle: startup, output streaming, cancellation, graceful/forced termination, exit code handling
  4. `src\SquadScout.Broker\Pty\PtySessionEnvelopePump.cs` — Translates PTY events (`Started`, `Output`, `Exited`) into broker `SessionLifecycle` and `OutputChunk` envelopes
  5. `src\SquadScout.Broker\Pty\PtySessionStartException.cs` — Startup failure semantics with diagnostics
  6. `tests\SquadScout.Broker.Tests\CopilotPtyHostTests.cs` — Comprehensive test coverage: happy path (real processes, chunking), failure modes (missing binary), cancellation safety, idempotency, integration envelope pump
- **Dependencies updated:** Added `Pty.Net` to broker csproj; updated appsettings.json with Copilot paths; registered in DI container
- **Test harness updated:** `tests\SquadScout.Broker.Tests\TestDoubles\MockPtyHarnessFixture.cs` now binds real PTY for integration validation while mock remains available
- **Seam preservation:** Direct spawn only (shell mode deferred per acceptance checklist), IPtyHost/IPtySession contracts unchanged, drop-in substitutable for MockPtyHost
- **Acceptance review:** Switch verified all 7 checklist items, confirmed no shell-path creep, confirmed output compatibility with relay layer
- **Verdict:** APPROVED for merge; unblocks Issue #6 Broker Relay Pipeline

### Issue #31 Revision — Aspire orchestration + ServiceDefaults (2026-03-25)

- **Shared defaults shape:** `src\SquadScout.ServiceDefaults` now multi-targets `net8.0;net10.0` so the broker, Functions isolated worker, and MAUI app can all share the same OpenTelemetry/logging + HttpClient resilience defaults without forcing Functions off `net8.0`.
- **Functions hosting seam:** Azure Functions Aspire integration is cleanest when `src\SquadScout.Functions\Program.cs` moves to `FunctionsApplication.CreateBuilder(args)` and calls `builder.AddServiceDefaults()` before the worker is built; that preserves isolated-worker behavior while letting Aspire orchestrate it with `AddAzureFunctionsProject`.
- **MAUI boundary:** `src\SquadScout.App` participates through ServiceDefaults + `IHttpClientFactory`, while `src\SquadScout.AppHost\AppHost.cs` registers the MAUI app via `AddMauiProject(...).AddWindowsDevice()` instead of trying to treat the mobile app like a hosted backend service.
- **Broker orchestration detail:** `src\SquadScout.Broker\Program.cs` must only call `UseUrls` when Aspire has not already injected server URLs, otherwise fixed local config overrides AppHost endpoint assignment.
- **Validation path:** `dotnet build .\SquadScout.slnx -nologo`, `dotnet test .\SquadScout.slnx -nologo --no-build`, and a smoke `dotnet run --project .\src\SquadScout.AppHost\SquadScout.AppHost.csproj --no-build` all succeeded after the Aspire revision.

### PR #46 Merge (2026-03-25T00:30:00Z)

- **Owner:** Link
- **PR:** rjygraham/SquadScout#46 "Add Aspire orchestration and ServiceDefaults"
- **Merge status:** ✅ Successfully merged to main using squash strategy
- **Rationale:** Single logical commit, clean history, minimizes downstream rebase conflicts
- **Result:** Aspire + ServiceDefaults now integrated into main; unblocks Phase 2 grain activation and multi-project orchestration testing
### Issue #6 Broker Relay Pipeline (2026-03-25)

- **Phase 1 relay owner lives above the PTY seam:** `src\SquadScout.Broker\Relay\InMemorySessionRelay.cs` now owns the active PTY session registry, project-root resolution, PTY startup, background envelope pumping, and client-input dispatch so both `MockPtyHost` and `CopilotPtyHost` stay swappable behind `IPtyHost`.
- **Input sequencing commit rule:** `src\SquadScout.Broker\Sessions\InMemorySessionOrchestrator.cs` now serializes client-envelope acceptance behind a per-session gate and only commits accepted client sequencing after the PTY write callback succeeds; duplicates stay idempotent and do not write twice.
- **Broker surface now exercises the datapath:** `src\SquadScout.Broker\Program.cs` starts sessions through the relay service and adds `/api/sessions/{sessionId}/input`, while `tests\SquadScout.Broker.Tests\SessionRelayPipelineTests.cs` proves start → input write → output publication → replay using the mock PTY harness.
- **Validation path:** `dotnet build .\SquadScout.slnx -nologo` and `dotnet test .\SquadScout.slnx -nologo --no-build` both pass after the relay pipeline landed.
### Issue #31 Revision — Aspire orchestration + ServiceDefaults (2026-03-25)

- **Shared defaults shape:** `src\SquadScout.ServiceDefaults` now multi-targets `net8.0;net10.0` so the broker, Functions isolated worker, and MAUI app can all share the same OpenTelemetry/logging + HttpClient resilience defaults without forcing Functions off `net8.0`.
- **Functions hosting seam:** Azure Functions Aspire integration is cleanest when `src\SquadScout.Functions\Program.cs` moves to `FunctionsApplication.CreateBuilder(args)` and calls `builder.AddServiceDefaults()` before the worker is built; that preserves isolated-worker behavior while letting Aspire orchestrate it with `AddAzureFunctionsProject`.
- **MAUI boundary:** `src\SquadScout.App` participates through ServiceDefaults + `IHttpClientFactory`, while `src\SquadScout.AppHost\AppHost.cs` registers the MAUI app via `AddMauiProject(...).AddWindowsDevice()` instead of trying to treat the mobile app like a hosted backend service.
- **Broker orchestration detail:** `src\SquadScout.Broker\Program.cs` must only call `UseUrls` when Aspire has not already injected server URLs, otherwise fixed local config overrides AppHost endpoint assignment.
- **Validation path:** `dotnet build .\SquadScout.slnx -nologo`, `dotnet test .\SquadScout.slnx -nologo --no-build`, and a smoke `dotnet run --project .\src\SquadScout.AppHost\SquadScout.AppHost.csproj --no-build` all succeeded after the Aspire revision.

### PR #46 Merge (2026-03-25T00:30:00Z)

- **Owner:** Link
- **PR:** rjygraham/SquadScout#46 "Add Aspire orchestration and ServiceDefaults"
- **Merge status:** ✅ Successfully merged to main using squash strategy
- **Rationale:** Single logical commit, clean history, minimizes downstream rebase conflicts
- **Result:** Aspire + ServiceDefaults now integrated into main; unblocks Phase 2 grain activation and multi-project orchestration testing

### Issue #14 Broker PubSub Session Routing Slice (2026-03-25)

- **Routing rule centralized:** `src\SquadScout.Broker\Relay\SessionGroupResolver.cs` now makes the broker consume the approved base session-group contract `session:{projectId}:{sessionId}` directly from shared contracts, while still leaving the optional `:brokerId` suffix dormant for later broker-affinity work.
- **Broker relay seam upgraded:** `src\SquadScout.Broker\Relay\AzureWebPubSubRelayPublisher.cs` and `AzureWebPubSubGroupClient.cs` now give the broker a real Azure Web PubSub publish path when `AzureWebPubSub:ConnectionString` is configured, and they explicitly track broker join/leave on session start/stop before and after PTY lifecycle traffic is published.
- **Inbound alignment without faking #11:** `src\SquadScout.Broker\Relay\InMemorySessionRelay.cs` now resolves the same session group for accepted client input so routing stays explicit and testable, but live MAUI join/leave plus actual Web PubSub command ingress still depend on issue #11’s missing client connection service.
- **Proof points:** `tests\SquadScout.Broker.Tests\SessionRelayPipelineTests.cs`, `AzureWebPubSubRelayPublisherTests.cs`, `SessionGroupResolverTests.cs`, and the updated `RecordingRelayPublisher` now cover approved group naming, broker join/leave tracking, and PTY message fan-out routing.
- **Validation:** `dotnet build .\SquadScout.slnx -nologo` and `dotnet test .\SquadScout.slnx -nologo --no-build` both pass in `D:\GitHub\SquadScout-14` after the routing slice.

### Issue #14 Reassessment After #11 Merge (2026-03-25)

- **Rebase checkpoint:** PR #48 / issue #11 is now merged on `main`, and `squad/14-pubsub-session-routing-group-membership` was rebased cleanly onto that state.
- **Original blocker removed:** `src\SquadScout.App\Services\MessagingConnectionService.cs` is no longer the old ready-state stub; it now negotiates and connects to Azure Web PubSub, so MAUI-side session-group join logic is present.
- **Remaining blocker made explicit:** Client input now goes out as `WebPubSubSendToGroupCommand`, but `src\SquadScout.Functions` still only exposes `NegotiateFunction` and the broker still only ingests live input via `POST /api/sessions/{sessionId}/input`. There is no inbound Web PubSub event/upstream handler forwarding group messages back into the broker.
- **Validation still green:** `dotnet test .\SquadScout.slnx -nologo --no-build --logger "console;verbosity=minimal"` passes with 76/76 tests (8 app, 68 broker), so the remaining problem is end-to-end routing completeness rather than a current unit-test failure.
- **Decision:** Do not open the #14 PR until the inbound command-ingress seam is implemented or the team explicitly narrows #14’s scope.

### Issue #14 Completion — Web PubSub Inbound Handler (2026-03-25)

- **Ingress seam closed:** `src\SquadScout.Functions\WebPubSubUpstreamFunction.cs` plus `src\SquadScout.Functions\Upstream\WebPubSubUpstreamHandler.cs` now accept Azure Web PubSub custom-event webhooks, return the required `WebHook-Allowed-Origin` validation header, deserialize client `InputChunkPayload` envelopes, and forward them into the existing broker `/api/sessions/{sessionId}/input` path.
- **Transport contract corrected:** `src\SquadScout.Contracts\Realtime\SessionUpstreamEventNames.cs` now defines the shared custom event name `session-input`, and `src\SquadScout.App\Services\MessagingConnectionService.cs` sends live client input with Web PubSub `event` frames instead of `sendToGroup`, because upstream handlers are only invoked for custom events.
- **Local orchestration wiring completed:** `src\SquadScout.Functions\Configuration\FunctionsHostOptions.cs`, `Program.cs`, `local.settings.sample.json`, and `src\SquadScout.AppHost\AppHost.cs` now carry an explicit broker base URL into the Functions host so local Aspire runs and standalone local settings both know how to reach the broker ingress endpoint.
- **Regression proof points:** `tests\SquadScout.Broker.Tests\PubSubUpstreamHandlerTests.cs` now covers successful forward, malformed envelopes, webhook validation, and broker conflict propagation; `tests\SquadScout.App.Tests\PubSubConnectionServiceTests.cs` now locks the app-side `event` command shape.

### Issue #16 Broker/PTy Gate Hardening (2026-03-25)

- **HTTP contract seam closed:** `src\SquadScout.Broker\Program.cs` now applies `src\SquadScout.Contracts\Messages\SessionMessageSerializer.cs` to ASP.NET Core HTTP JSON options so broker endpoints accept the same camelCase + string-enum envelope shape already used by Functions, MAUI, and relay serialization.
- **Repeatable gate proof added:** `tests\SquadScout.Broker.Tests\BrokerPhase1DatapathGateTests.cs` uses `WebApplicationFactory<Program>` with a seeded `InMemoryProjectCatalog`, `MockPtyHost`, and `RecordingRelayPublisher` to drive real HTTP session start/input through PTY writes, sequenced broker publication, and replay verification.
- **Phase 1 broker assumption made explicit:** The broker-side path is ready for a repeatable gate as long as upstream callers send `MessageEnvelope<InputChunkPayload>` using the shared serializer contract; the remaining cross-team work stays on MAUI/Functions ingress coordination rather than PTY/replay correctness.
- **Validation:** `dotnet test .\tests\SquadScout.Broker.Tests\SquadScout.Broker.Tests.csproj -nologo --no-build`, `dotnet build .\SquadScout.slnx -nologo`, and `dotnet test .\SquadScout.slnx -nologo --no-build` all pass after the broker JSON-alignment fix and new gate tests landed.

### Issue #16 WS-B2 — Advisory Client Gap Policy (2026-03-25)

- **Policy change landed:** Broker-side `GapDetected` client envelopes are now treated as advisory for Phase 1 ingress. The broker still returns structured gap diagnostics, but it no longer drops the user input or returns `409 Conflict` when the PTY write is accepted.
- **Ack safety preserved:** `src\SquadScout.Broker\Sessions\SessionSequenceValidator.cs` and the session runtime state still freeze cumulative acknowledgement on gap-detected envelopes, so the broker never advances ack past missing client frames.
- **Observable behavior improved:** `src\SquadScout.Broker\Sessions\InMemorySessionOrchestrator.cs` now logs an explicit structured warning when a gap-detected input is forwarded: session id plus expected/received client sequence values.
- **Proof path updated:** `tests\SquadScout.Broker.Tests\SessionSequenceValidatorTests.cs`, `SessionRelayPipelineTests.cs`, and `BrokerPhase1DatapathGateTests.cs` now prove acceptance semantics, PTY forwarding, and HTTP 200 behavior for advisory gap handling.
- **Validation:** Focused broker tests passed, followed by `dotnet test .\SquadScout.slnx -nologo --no-build` with **115/115 passing**.

### Issue #17 Phase 1 Session Telemetry & Replay Diagnostics (2026-03-25)

- **Authoritative export lives in broker runtime:** `src\SquadScout.Broker\Sessions\SessionRuntimeState.cs` now keeps bounded, secret-safe telemetry buffers for recent envelopes and replay/validation events, then exports them as `SessionTelemetrySnapshot` via `ISessionOrchestrator.ExportTelemetryAsync(...)`.
- **Sequence-centric continuity context added:** Each export carries the active sequencing snapshot, replay-buffer window metadata, redacted payload previews, stable `messageId` / `correlationId` / `causationId` values, replay gap details, and generation-reset events so Phase 1 ordering failures can be reconstructed without ad hoc console logs.
- **Lightweight local hook exposed:** `src\SquadScout.Broker\Program.cs` now serves `GET /api/sessions/{sessionId}/telemetry`, which returns the runtime-authored diagnostics snapshot for local failure analysis without introducing a second telemetry model or app-only state dependency.
- **Secret-safety tightened for diagnostics:** `src\SquadScout.Contracts\Security\SecretRedactor.cs` now redacts quoted JSON key/value pairs and `JsonElement` payloads, which keeps exported telemetry safe even when PTY output or client input contains embedded JSON secrets.
- **Proof path:** `tests\SquadScout.Broker.Tests\SessionTelemetrySnapshotTests.cs`, `BrokerPhase1DatapathGateTests.cs`, and `SecurityBaselineTests.cs` now cover replay-gap export context, generation-reset telemetry, HTTP export behavior, and secret redaction.
- **Validation:** Focused broker diagnostics tests passed (40/40), followed by `dotnet build .\SquadScout.slnx -nologo` and `dotnet test .\SquadScout.slnx -nologo --no-build` passing for the full solution.

### Issue #17 Landing Team Coordination (2026-03-25T21:41:50Z)

- **Architect role:** Validated Issue #17 diagnostics architecture against Phase 1 requirements. Broker session runtime as authoritative source. Lightweight JSON export seam (`GET /api/sessions/{sessionId}/telemetry`). Reuse existing correlation fields without parallel model.
- **Follow-on constraints verified:** Secret-safety enforced ✅. Buffers stay bounded ✅. Future app/admin surfaces will consume broker-authored export ✅.
- **Cross-team alignment:** Neo (landing execution), Switch (acceptance gate), Morpheus (hardening review), Scribe (decision consolidation). All validation greens converged.
- **Session log:** 2026-03-25T21-41-50Z-issue-17-landing.md. Orchestration: 2026-03-25T21-41-50Z-switch.md (gate), 2026-03-25T21-41-50Z-neo.md (execution). Decision inbox merged and cleared. Ready for Phase 2 handoff.


### 2026-03-25: Phase 1 WorkingDirectory Analysis Complete (Orchestration Log)

**Status:** Issue #59 created as low-priority clarification item.

- **Finding:** Core functionality already implemented correctly; behavior matches user intent
- **Gap Analysis:** Remaining work is observability and documentation only
- **Implementation Plan:** Minimal approach (logging + XML documentation; no behavioral changes)
- **Key Learning:** Distinguish implementation from clarification to avoid duplicate issues; honest framing prevents rework

**Next:** Link can begin Issue #59 as low-priority polish, no blockers.
