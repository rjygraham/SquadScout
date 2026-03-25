# Switch History

## Day 1 Context

- User: Ryan Graham
- Project: Brokered remote access to GitHub Copilot from a .NET MAUI app.
- Stack: .NET host process, Azure Web PubSub, Entra-backed auth, Azure Function token issuance, and possible Orleans replay state.
- Key concerns: reconnect tests, dropped-connection recovery, session start behavior, and cross-component regression coverage.

## Learnings

### Workstream Decomposition (2026-03-24)

**Testing strategy is multiaxial:**
- PTY bridge integration must be proven first, without Orleans state complexity
- Reconnect/replay are primary reliability vectors, not Phase 4 afterthoughts
- Message ordering is app-level responsibility, not delegated to PubSub
- Observability must enable post-mortem reproduction (telemetry + session export)
- Voice I/O (TTS/STT) is a cross-cutting concern affecting session lifecycle and message ordering

**First executable slice should be transport-minimal:**
- Unit tests for envelope format, sequence numbering, circular buffer logic
- Local e2e test: broker + mock PTY + MAUI client, no external services
- Success: <5s execution, zero external dependencies, clear diagnostics
- This unblocks Phase 1 implementation and validates core assumptions

**Blocking dependencies:**
- Link owns message envelope format (envelope must support sequence, timestamp, gap detection)
- Neo owns Orleans SQLite schema and Copilot spawn decision
- Seraph owns session connection/disconnection lifecycle (what triggers grain activation/deactivation?)
- Morpheus owns terminal rendering strategy (SkiaSharp vs. WebView) and voice I/O blocking guarantees

**Test artifact requirements are non-negotiable:**
- No secret logging in PTY paths
- Sequence-centric debug model (every issue maps to gap, ordering, or reconnect)
- Deterministic replay support (session export includes all PubSub events with timestamps)
- Phase 1 tests must run offline (mock PubSub, mock PTY, no Azure services)


### User Directives Accepted (2026-03-24)

- **Message envelope locked to shared source project (SquadScout.Contracts).** Directly impacts WS-1 (contract) execution. No NuGet package abstraction needed for MVP.
- **First executable slice confirms test strategy.** Days 1–5 with zero external dependencies validates sequence/replay correctness before Orleans or PubSub complexity.
- **Status:** All 6 Switch workstreams (envelope design, property-based tests, mock harness, reconnect tests, observability instrumentation, voice I/O cross-cutting) aligned to Neo's unified plan. WS-1 is critical path, starts immediately with Morpheus.

### 2026-03-24T17:31:17Z — GitHub Issues Backlog Imported

- **Import context:** Neo created GitHub issues #1–#34 in rjygraham/SquadScout with full phase gate preservation and routing labels.
- **Switch ownership:** Issues #3 (Sequence validator), #4 (Mock PTY), #16 (Phase 1 E2E test gate), #24 (Grain & reconnect test gate), #29 (Voice test harness), #33 (Diagnostic harness).
- **Label pattern:** All issues tagged with `squad` + owner label (e.g., `squad:switch`) + phase label (e.g., `phase:1`/`phase:2`/`phase:4`).
- **Critical path:** Issue #3 (Sequence Validator) unblocks Issues #2 (Message Envelope), #4 (Mock PTY), and #16 (E2E gate).
- **Gate responsibilities:** Switch owns #16 (Phase 1→2 gate) and #24 (Phase 2→3 gate). These issues require all dependent workstreams to pass before proceeding.
- **Status:** All team histories updated. Ready for test strategy kickoff and Phase 1 test harness creation.

### Issue #2 Message Envelope Contract (2026-03-25)

- **Shared contract path:** Session envelope types now live under `src\SquadScout.Contracts\Messages\` and use `SessionMessageSerializer.DefaultOptions` for stable camelCase + string-enum JSON across components.
- **Replay safety pattern:** `ReplayResponsePayload` exposes requested range, available replay window, and `GapDetected` so reconnect failures can surface overflow explicitly instead of corrupting transcript state silently.
- **Single source of truth:** Sequence acknowledgements stay on `MessageEnvelope<TPayload>.AcknowledgedSequence`; heartbeat payloads carry only liveness metadata to avoid duplicate ack state.
- **Contract coverage:** Representative wire-contract tests live in `tests\SquadScout.Broker.Tests\MessageEnvelopeContractTests.cs`, including serialization shape, replay snapshot shape, and optional-field deserialization for backward-compatible additions.

### Issue #2 Contract Batch Parallel Execution (2026-03-25)

**Switch outcome (implementation + test):**
- Implemented `MessageEnvelope<TPayload>` generic shape with acknowledgement as single source of truth
- Established shared JSON serialization (camelCase + string-enum) in `SessionMessageSerializer.DefaultOptions`
- Replay responses explicitly surface available window and gap detection (`AvailableFromSequence`, `AvailableToSequence`, `GapDetected`)
- Backward compatibility rule locked for contract v1: additive optional members allowed; renames/removals/semantic changes require major version bump
- Committed as b4efdab, opened draft PR #36 (closes #2)
- All tests pass; solution builds cleanly
- **Awaiting:** Morpheus review feedback from comprehensive risk pass (8-point checklist + 7 ambiguities in `.squad/decisions.md`)
- Next: Incorporate blocker resolutions before final reviewer signoff

### Formal Review Outcome — Issue #2 / PR #36 (2026-03-25T18:43:27Z)

**REJECTED by Morpheus.** Build & test pass confirmed; ack duplication and gap reporting resolved. Two critical semantic blockers prevent merge:

1. **Sequence ownership undefined:** Single `Sequence` field for both directions; contract does not reserve replay domain for broker-only or clarify direction scope. Risks client frames in replay buffer.
2. **Replay reset boundary missing:** No generation/epoch marker. Reconnecting client cannot distinguish resumed state from fresh stream after broker/PTY restart.

**Revision assignment:** Link owns next iteration; Switch locked out for this cycle. Morpheus will re-review before merge.

**Patterns to preserve:** Single-source-of-truth acknowledgement (top-level only), heartbeat liveness-only separation. These resolved correctly and should not regress.

**Impact:** Blocks Phase 2 grain activation and replay buffer. Message envelope is critical path; decision gate remains locked until both blockers resolved.

### Issue #3 Acceptance Bar (2026-03-25)

- **Reliability core paths:** Issue #3 lives primarily in `src\SquadScout.Broker\Sessions\` (`SessionSequenceValidator`, `CircularReplayBuffer`, `SessionRuntimeState`, `InMemorySessionOrchestrator`) with focused coverage in `tests\SquadScout.Broker.Tests\`.
- **Tester review pattern:** Reviewer signoff must treat client-sequence monotonicity, cumulative ack semantics, and broker replay-window behavior as three separate invariants. Green happy-path tests are not enough unless duplicate, gap, overflow, and generation-boundary behavior are explicit.
- **Current strong coverage:** Branch already exercises first-sequence acceptance, new-message ack regression rejection, overflow gap reporting, reset-boundary replay, heartbeat exclusion from replay, and session-id trust-boundary checks.
- **Remaining high-risk coverage to demand:** Duplicate frames with changed ack, gap frames with ack interaction, future-generation validation, project-id mismatch, invalid replay request bounds (`FromSequenceInclusive`, `MaximumMessages`), and deterministic multi-page replay continuation.
- **Verified commands for this review bar:** `dotnet build .\SquadScout.slnx -nologo`, focused broker test filter for `SessionSequenceValidatorTests|CircularReplayBufferTests|InMemorySessionOrchestratorReplayTests`, then `dotnet test .\SquadScout.slnx -nologo --no-build`.

### Formal Review Outcome — Issue #3 / PR #37 (2026-03-25T19:30:09Z)

- **Artifact reviewed:** PR #37 commit `8068e81` on `squad/3-sequence-validator-replay-buffer`; current branch HEAD only adds Scribe documentation on top of that implementation.
- **Verification run:** `dotnet build .\SquadScout.slnx -nologo` passed cleanly, focused broker sequencing/replay tests passed (12/12), and full solution tests passed (20/20).
- **Implementation strengths confirmed:** broker-owned monotonic sequencing, cumulative ack high-water tracking, 500-message circular replay model, explicit overflow/gap reporting, generation reset boundaries, heartbeat exclusion from replay, and session-id trust-boundary rejection.
- **Blocking reviewer gap:** explicit failure-mode coverage is still missing for `FutureGeneration` validation and for replay rejection on project-id mismatch. The implementation paths exist (`src\SquadScout.Broker\Sessions\SessionSequenceValidator.cs`, `src\SquadScout.Broker\Sessions\InMemorySessionOrchestrator.cs`), but Switch charter does not allow approval without those tests.
- **Recommended next reviser:** Link. Morpheus authored the artifact and is locked out for the next correction cycle after rejection.
- **Verdict:** **REJECTED** — No implementation bug found; coverage is incomplete. Two explicit test gaps block approval until next revision cycle.

### Morpheus Issue #3 Delivery (2026-03-24)

- **Branch:** `squad/3-sequence-validator-replay-buffer` (commit `8068e81`)
- **Scope:** In-memory reliability core: monotonic client validation, 500-message replay buffer, overflow/gap reporting, generation reset boundaries, trust-boundary checks
- **Implementation:** Broker session state + replay orchestrator with focused test suite
- **Decision gates merged:** Replay buffer scope, overflow behavior, generation reset boundary, trust-boundary enforcement all documented in `.squad/decisions.md`
- **Next:** Incorporate Switch's remaining high-risk coverage before signoff; merge to unblock Phase 2 grain implementation

### Issue #4 Mock PTY Harness (2026-03-25)

- **Reusable PTY seam:** Phase 1 now has a broker-local PTY abstraction under `src\SquadScout.Broker\Pty\` with `IPtyHost`, `IPtySession`, `PtySessionStartRequest`, and `PtySessionEvent`. The seam stays lifecycle/stream oriented so issue #5 can swap in a real Copilot host without replay refactoring.
- **Deterministic harness pattern:** `MockPtySession` uses logical ticks plus explicit `ReleaseNext()` / `AdvanceBy()` controls instead of wall-clock sleeps. That keeps chunking, exit injection, and cancellation coverage stable in CI and fast locally.
- **Relay/test fixture pattern:** `tests\SquadScout.Broker.Tests\TestDoubles\MockPtyHarnessFixture.cs` pumps PTY lifecycle/output events through `InMemorySessionOrchestrator` and `RecordingRelayPublisher` so tests can assert sequencing, relay publication order, replay windows, and session state transitions in one offline slice.
- **Contract additions:** Typed payloads for `Input`, `Output`, and `SessionLifecycle` now live in `src\SquadScout.Contracts\Messages\`. `SessionRuntimeState` updates `SessionDescriptor.State` from `SessionLifecyclePayload`, and `IRelayPublisher` can now capture broker envelopes via `PublishEnvelopeAsync`.
- **Verification:** `dotnet build .\SquadScout.slnx -nologo` and `dotnet test .\SquadScout.slnx -nologo --no-build` both pass with 28/28 tests, including the new `MockPtySessionTests` and `MockPtyHarnessIntegrationTests`.

### Issue #5 Acceptance Bar (2026-03-25)

- **Current branch state:** `squad/5-copilot-pty-host` implementation is complete.
- **Seam to preserve:** The real host must remain a drop-in implementation of `src\SquadScout.Broker\Pty\IPtyHost.cs` / `IPtySession.cs`, emitting only `PtySessionEvent` values (`Started`, `Output`, `Exited`) and keeping broker sequencing/replay ownership out of the PTY layer.
- **Existing compatibility proof:** `tests\SquadScout.Broker.Tests\TestDoubles\MockPtyHarnessFixture.cs` already proves the broker can translate PTY lifecycle/output events into `SessionLifecyclePayload` and `OutputChunkPayload` via `InMemorySessionOrchestrator`; issue #5 should reuse that event shape rather than inventing a parallel transport.
- **Reviewer bar:** Signoff must require explicit tests for direct spawn success, startup failure surfacing, pre-start cancellation, deterministic teardown/idempotent termination, exit-code reporting, and chunked output streaming through the current seam. Shell mode remains explicitly deferred and should stay out of implementation and tests for this issue.

### Issue #5 Formal Review — APPROVED (2026-03-25)

- **Artifact:** `squad/5-copilot-pty-host` (current workspace state)
- **Verdict:** **APPROVED**
- **Verification:**
  - `CopilotPtyHost` correctly implements `IPtyHost` using `sch.pty.net`.
  - `CopilotPtySession` handles lifecycle events (`Started`, `Output`, `Exited`) without bleeding implementation details.
  - Tests `CopilotPtyHostTests.cs` cover all required scenarios:
    - Direct spawn (Happy path, exit codes, chunking).
    - Startup failure (Missing executable).
    - Pre-start cancellation.
    - Idempotent termination.
    - Integration with `PtySessionEnvelopePump`.
- **Decision:** Ready for merge. Blocks for Issue #6 removed.

### Issue #5 Session Completion Log (2026-03-24T21:36:52Z)

- **Review Context:** Formal review completed 2026-03-25. Artifact: `squad/5-copilot-pty-host` with full CopilotPtyHost implementation, test coverage, and integration proofs.
- **Reviewer Decision:** **APPROVED** for merge to main.
- **Acceptance Checklist Verification (7/7):**
  1. ✓ Direct spawn lifecycle explicit (start request validation, launch, ready event)
  2. ✓ Current seam intact (IPtyHost/IPtySession contracts unchanged, drop-in substitutable)
  3. ✓ Output streaming compatible (ordered Output events, OutputChunkPayload translation)
  4. ✓ Startup failures surfaced cleanly (exceptions for missing binary, bad paths)
  5. ✓ Cancellation and teardown safe (pre-start cancellation proven, TerminateAsync idempotent)
  6. ✓ Exit semantics readable (graceful, non-zero, forced termination with correct codes)
  7. ✓ No shell-path creep (direct spawn only, no shell wrapping, tests confirm constraint)
- **Highest-Risk Scenarios Covered:**
  - Started before usable: proven safe with integration tests
  - Startup cancellation leaks: proven safe, no child process remains
  - Startup failure swallowed: explicit exception reporting
  - TerminateAsync race condition: idempotency tested
  - PTY output chunking: tested against real processes (powershell.exe)
  - stderr/startup noise: preserved in Output events
  - Immediate non-zero exit: detected as startup failure, not false success
  - Shell invocation: deferred, no creep observed
- **Deliverables approved for merge:**
  - Configuration: `CopilotPtyHostOptions.cs` (DI config)
  - Implementation: `CopilotPtyHost.cs`, `CopilotPtySession.cs`, `PtySessionEnvelopePump.cs`, `PtySessionStartException.cs`
  - Tests: `CopilotPtyHostTests.cs` (comprehensive coverage)
  - Dependencies: Pty.Net added, broker DI updated, appsettings configured
- **Phase 1 Unblocked:** This approval unblocks Issue #6 (Broker Relay Pipeline) to proceed with translating real PTY events into relay publication.

### Issue #9 Formal Review — APPROVED (2026-03-24)

- **Artifact reviewed:** PR #43 / branch `squad/9-maui-app-shell-scaffolding` at commit `84d7300`
- **Verdict:** **APPROVED**
- **Validation:** `dotnet build .\SquadScout.slnx -nologo`, `dotnet test .\SquadScout.slnx -nologo --no-build`, and the previously failing `dotnet test .\SquadScout.slnx -nologo` all passed in `D:\GitHub\SquadScout-9` (36/36 tests on the full solution).
- **Reviewer read on scope:** The shell now has the intended single-user flow (`projects` → `active-session`), app-side composition points are registered for auth/messaging/project/session seams, and Development settings provide a stable offline review path without reshaping the host.
- **Key regression callout:** The MAUI app icon/resizetizer failure did not reproduce; the full `dotnet test .\SquadScout.slnx -nologo` run built `SquadScout.App` for Windows and Android successfully, so issue #9 no longer blocks on duplicate `appicon` output.
- **Testing pattern worth preserving:** `tests\SquadScout.App.Tests\SquadScout.App.Tests.csproj` links app configuration/service source files into a plain `net10.0` test project. That keeps the fallback/state logic testable without reintroducing MAUI resource build conflicts.
- **Merge-risk note:** Coverage is strongest around fallback services and active-session state updates, not page-level UI automation. That is acceptable for this scaffolding issue because the navigation/state seams are now in place and the full solution build remains green.

### Issue #9 Review Completion — Handoff to Trinity (2026-03-25)

- **Timestamp:** 2026-03-25T23:57:15Z
- **Review scope:** PR #43 final approval gate and merge-watch handoff
- **Validation performed:** Three-command test suite on `D:\GitHub\SquadScout-9` (build, test-no-build, test-full)
- **All pass verdicts:** `dotnet build .\SquadScout.slnx -nologo`, `dotnet test .\SquadScout.slnx -nologo --no-build`, `dotnet test .\SquadScout.slnx -nologo` (36/36)
- **Appicon/resizetizer regression:** **Clear** — no duplicate output detected during full test run. Windows and Android MAUI builds exercise successfully.
- **Design confidence:** Shell nav/state seams are wired. Auth, messaging, project, session lifecycle composition points registered. Dev config supports reviewable offline path.
- **Verdict:** **APPROVED** — Ready for immediate merge.
- **Handoff:** Orchestration logs written. Decisions merged. Trinity picks up merge-watch for CI/post-merge validation and regression monitoring.
- **Status:** Phase 1 Wave 1 Issue #9 complete. Advance team to next workstream (PR merge and Phase 2 Orleans integration setup).

### Aspire / ServiceDefaults Review Gate — REJECTED (2026-03-25T00:05:25Z)

- **Requested scope:** Add .NET Aspire orchestration and ServiceDefaults across the solution plus `SquadScout.App`, `SquadScout.Broker`, and `SquadScout.Functions`.
- **Handoff result:** No valid Seraph handoff existed. No PR was opened, no acceptable handoff report was recorded, and the local worktree `seraph/issue-31-aspire-service-defaults` was a clean copy of `origin/main` with zero code diff.
- **Validation run anyway:** `dotnet build .\SquadScout.slnx -nologo` and `dotnet test .\SquadScout.slnx -nologo --no-build` both passed in `D:\GitHub\SquadScout\.worktrees\seraph-issue-31-aspire-service-defaults` (55/55 tests).
- **Concrete blockers:** no `AppHost` project, no `ServiceDefaults` project, no Aspire/service-discovery/OpenTelemetry wiring in broker or functions startup, no MAUI-side integration point, and no reviewable diff for solution-structure changes.
- **Tracking inconsistency:** the branch name references issue `#31`, but GitHub issue `#31` is currently structured logging/correlation IDs, not Aspire / ServiceDefaults.
- **Revision owner recommendation:** Link should own the next revision because the missing work is primarily solution/orchestration scaffolding; Seraph is locked out for the next revision cycle on this artifact.

### Issue #12 Token Validation & Session Claims — Morpheus Complete (2026-03-25T00:20:09Z)

**Status:** Implementation complete. Token validation middleware and session claims hardening integrated. PR #45 opened (closes #12). Ready for Switch formal review gate.

**Morpheus Deliverables:**
- `src\SquadScout.Broker\Middleware\TokenValidationMiddleware.cs` — Bearer token extraction, validation, expiration enforcement
- `src\SquadScout.Broker\Services\SessionClaimsValidator.cs` — Session claim verification, project ownership binding
- Token lifecycle: extraction from HTTP headers, signature verification, expiration checks
- Claims binding: project ownership, user context, immutability constraints
- Broker authentication hardening integrated with session state machine
- Full test coverage; all tests passing

**Build & Test Status:**
- ✅ Solution builds cleanly
- ✅ All tests pass (baseline maintained)
- ✅ Branch: `squad/12-token-validation-session-claims-hardening`
- ✅ Commit: 5e7f232
- ✅ PR: #45 (no conflicts, ready for review)

**Handoff:** WS-2 token validation critical path. Morpheus awaits Switch formal review gate on PR #45. If revision needed, Link assumes ownership per team protocol.

### Issue #13 Formal Review — REJECTED (2026-03-25)

- **Artifact reviewed:** PR #44 / branch `squad/13-broker-session-start-stop-endpoints` at commit `bb59652`.
- **Validation run:** `dotnet build .\SquadScout.slnx -nologo` passed, focused `SessionRelayPipelineTests` passed (8/8), and full solution tests passed (59/59).
- **Blocking finding 1:** `InMemorySessionRelay.StopAsync(...)` and `RelayInputAsync(...)` do not share a serialization point. Input checks `IsStopRequested` before entering the orchestrator gate, so a request already in flight can still reach `WriteAsync(...)` after stop has been accepted.
- **Blocking finding 2:** stop-related input rejection returns a generic 409 `{ message }` via `InvalidOperationException` instead of the new structured `SessionControlException` shape. That leaves future clients without a stable machine-readable lifecycle code for the "stopping" case.
- **Coverage gap that matters:** `SessionRelayPipelineTests` prove completed-stop, already-exited, and project-mismatch behavior, but do not exercise stop-in-flight input rejection or concurrent stop overlap. For lifecycle work, happy-path stop coverage is not enough.
- **Recommended next reviser:** Morpheus. Link authored the artifact and should sit out the next correction cycle.
