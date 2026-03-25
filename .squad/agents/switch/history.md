# Switch History

## Day 1 Context

- User: Ryan Graham
- Project: Brokered remote access to GitHub Copilot from a .NET MAUI app.
- Stack: .NET host process, Azure Web PubSub, Entra-backed auth, Azure Function token issuance, and possible Orleans replay state.
- Key concerns: reconnect tests, dropped-connection recovery, session start behavior, and cross-component regression coverage.

## Learnings

- **Web PubSub Upstream Auth:** The upstream handler uses `WebPubSubUpstreamAuthenticator` which supports both `WebHook-Signature` (HMAC-SHA256) and Managed Identity (Easy Auth).
- **Security Pattern:** Use `CryptographicOperations.FixedTimeEquals` for signature verification to prevent timing attacks.
- **Azure Functions Identity:** Trusting `x-ms-client-principal-id` requires verifying `WEBSITE_INSTANCE_ID` is present to ensure the request is from the Azure platform (Easy Auth boundary).
- **Testing:** `PubSubUpstreamHandlerTests` uses a `DelegateHttpMessageHandler` to mock `HttpClient` responses, allowing full integration-style testing of the handler logic without a real broker.
- **MAUI app test harness:** `tests\SquadScout.App.Tests\SquadScout.App.Tests.csproj` links selected `src\SquadScout.App` source files directly, so new view-model tests can exercise production logic without referencing the MAUI app project.
- **MAUI seam pattern:** Lightweight test doubles for `IAppNavigator` plus a local `MainThread` shim are enough to unit-test `ProjectSelectionViewModel` and `ActiveSessionViewModel` in `tests\SquadScout.App.Tests\ProjectSelectionViewModelTests.cs` and `tests\SquadScout.App.Tests\ActiveSessionViewModelTests.cs`.
- **Issue #16 gate spine:** The Phase 1 datapath gate is best anchored in six artifacts: `tests\SquadScout.App.Tests\PubSubConnectionServiceTests.cs`, `tests\SquadScout.App.Tests\SessionTranscriptControllerTests.cs`, `tests\SquadScout.Broker.Tests\PubSubNegotiateEndpointTests.cs`, `tests\SquadScout.Broker.Tests\PubSubUpstreamHandlerTests.cs`, `tests\SquadScout.Broker.Tests\SessionRelayPipelineTests.cs`, and `tests\SquadScout.Broker.Tests\MockPtyHarnessIntegrationTests.cs`.
- **Shortest repeatable gate path:** For local confidence, run focused app transport tests, focused broker datapath tests, then `dotnet test .\SquadScout.slnx -nologo` as the final regression pass.
- **Client transport test pattern:** In `tests\SquadScout.App.Tests\PubSubConnectionServiceTests.cs`, the safest way to prove receive-loop state is to inject broker frames through `FakeWebPubSubSocket`, then inspect the next outbound client envelope to confirm generation and cumulative ack behavior after gaps or generation resets.
- **Gate posture:** Switch should not approve the Phase 1 datapath without explicit failure-mode coverage on the MAUI receive path and concrete diagnostics handoffs for broker/function/client boundaries.


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

### PR #57 Review (feature/issue-17-session-telemetry-diagnostics) — APPROVED (2026-03-26)

**Scope:** Issue #17 (Phase 1 Session Telemetry & Replay Diagnostics) — Final PR review for merge approval

**Implementation verified:**
- 126/126 tests pass (app + broker)
- Clean build with zero warnings or errors
- All claimed coverage areas validated through direct code inspection

**Failure-mode coverage confirmed:**
- Replay buffer overflow with gap detection (`BrokerPhase1DatapathGateTests.InputEndpointReturnsSuccessForGapDetectedClientInputAndStillWritesToPty`)
- Secret redaction in telemetry export (`BrokerPhase1DatapathGateTests.TelemetryEndpointExportsSecretSafeSessionDiagnostics`, `SessionTelemetrySnapshotTests.ExportTelemetryRedactsSensitivePayloadPreview`)
- Generation reset boundary detection (`InMemorySessionOrchestratorReplayTests.ReplayReturnsResetBoundaryWhenGenerationChanges`)
- Gap-aware warning status (200 OK, not 409 Conflict)
- Client forward failure tracking with exception capture
- Heartbeat exclusion from replay buffer (`InMemorySessionOrchestratorReplayTests.ReplayDetectsOverflowAndSkipsHeartbeatControlFrames`)

**Observability additions verified:**
- `SessionTelemetryBuffer<T>` circular buffer (capacity: 32 envelopes, 64 events)
- `SessionTelemetrySnapshot` export with `RecentEnvelopes`, `RecentEvents`, `ReplayBuffer` telemetry
- Broker telemetry export endpoint (`GET /api/sessions/{sessionId}/telemetry`)
- Structured logging for gap detection (Warning level, includes expected/received/ack context)
- App-side recent traffic tracking for client diagnostics

**Security baseline maintained:**
- All secret redaction patterns validated (`SecretRedactor` handles passwords, tokens, JWTs, Authorization headers, connection strings, credentialed URIs, GitHub PATs)
- No raw payload logging in production paths
- Payload previews truncated to 512 characters with redaction applied

**Phase 1 gate completion verified:**
- Full input → PTY → replay → telemetry round-trip covered in `BrokerPhase1DatapathGateTests`
- Client reconnect/replay path tested in `PubSubConnectionServiceTests`
- Session lifecycle event tracking proven in orchestrator tests

**Verdict:** ✅ **APPROVED** — PR #57 meets all Switch charter requirements. Failure-mode coverage is explicit and complete. Telemetry export is secret-safe. Phase 1 diagnostics baseline is now production-ready. Cleared for merge.

**Non-blocking observations:**
- Telemetry export endpoint has no rate limiting (acceptable for Phase 1, defer to Phase 2 if needed)
- Circular buffer sizes are reasonable for diagnostic purposes (32 envelopes, 64 events)
- Logger is optional dependency in orchestrator (uses NullLogger fallback when not provided)

**Next:** Ryan or Neo can merge PR #57 to main. Issue #17 closes on merge.

### Issue #17 Landing Review — APPROVED (2026-03-25)

**Scope:** Issue #17 (Phase 1 Session Telemetry & Replay Diagnostics)

**Product Changes:**
- `SessionTelemetryBuffer.cs`, `SessionTelemetrySnapshot.cs` — circular buffer for recent envelopes/events with secret-safe export
- Broker orchestration telemetry export API (`GET /api/sessions/{sessionId}/telemetry`)
- `SecretRedactor` enhanced with pattern-based JSON redaction for structured payload previews
- App-side recent envelope tracking for client diagnostics
- Gap detection validation status returns 200 OK (accepted with warning) instead of 409 Conflict

**Test Coverage:**
- `BrokerPhase1DatapathGateTests` — 3 scenarios covering full input → PTY → replay → telemetry round-trip
- `SessionTelemetrySnapshotTests` — 2 scenarios for export structure and secret redaction
- Hardening tests from prior commit (7 focused tests: ack idempotency, trust boundaries, generation resets)

**Validation Gates:**
- ✅ Clean build (`dotnet build .\SquadScout.slnx -nologo`)
- ✅ All tests pass (126/126 app + broker)
- ✅ Secret-safe export validated in dedicated tests
- ✅ Phase 1 datapath gate covers input → PTY → replay → telemetry

**Failure-Mode Coverage:**
- Replay window capture (AvailableFromSequence, AvailableToSequence)
- Generation reset events with reason field
- Sequence gap detection in recent event buffer
- All payload previews redacted for secret patterns (passwords, tokens, JWTs, Authorization headers)

**Verdict:** ✅ **APPROVED** — Single coherent landing. Issue #17 acceptance criteria met with full test coverage. Ready for commit + PR + merge.

**Next:** Neo stages commit excluding `.squad/` bookkeeping, opens PR against main with Issue #17 closure reference, pushes to origin.

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

### Issue #16 WS-B1 Replay Recovery (2026-03-25)

- **Replay request trigger rule:** In `MessagingConnectionService`, broker sequence gaps and reconnect completion must both emit a `ReplayRequest` envelope immediately; otherwise the replay contract exists but the client never exercises it.
- **Receive-loop deadlock warning:** Replay requests triggered from inside the socket receive loop cannot synchronously await Web PubSub command acks. The ack arrives on the same receive loop, so replay dispatch from gap detection must be queued asynchronously or it deadlocks the transport.
- **Gap closure rule:** A single `_gapDetected` flag is not enough to prove recovery. The client also needs the highest observed broker sequence for the current generation so replayed messages can clear the warning only when cumulative ack catches back up to the observed high-water mark.
- **Durable user warning path:** A `ReplayResponse` with `GapDetected=true` is best surfaced through `MessageConnectionStatus` as a faulted continuity warning, because the existing transcript UX already renders faulted transport state durably without requiring a separate UI seam.
- **Validation note:** Replay-focused app tests pass with `dotnet test .\tests\SquadScout.App.Tests -nologo --no-build --filter "ReceiveLoopTracksGenerationGapAndUsesRecoveredBrokerAckForLaterInput|ReconnectAsyncReNegotiatesAfterUnexpectedDisconnect|ReceivingBrokerMessagesWithSequenceGapSetsGapDetectedStatus|ReceivingNewGenerationResetsClientAcknowledgementState|ReplayResponseGapDetectedSurfacesDurableWarning|SendInputAsyncPublishesClientEnvelopeWithLatestAcknowledgedSequence|PrepareForSessionAsyncNegotiatesAndConnectsToSessionGroup|PrepareForSessionAsyncReturnsFaultedStatusWhenNegotiateFails|PubSubNegotiationClientAddsDevelopmentHeadersForLoopbackNegotiation"`; unrelated Trinity-owned token-refresh tests in the same file still fail in the wider app filter.
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

### Issue #12 Formal Review — APPROVED (2026-03-26)

- **Artifact reviewed:** PR #45 / branch `squad/12-token-validation-session-claims-hardening` at commit `5e7f232`.
- **Validation run:** `dotnet build .\SquadScout.slnx -nologo`, focused `PubSubNegotiateEndpointTests` (14/14), and full solution tests (61/61) all passed in `D:\GitHub\SquadScout-12`.
- **Security read:** Easy Auth headers are only trusted inside the Functions host boundary, mismatched header/payload principals are rejected, client requests cannot self-assert `brokerId`, and PubSub `userId` values are session-scoped (`participant:project:session[:broker]:principal`).
- **Contract read:** Negotiation response surface is tighter (no echoed principal details/roles/groups), and no checked-in client/broker consumer depends on the removed fields.
- **Merge-risk note:** No GitHub checks are attached to PR #45 yet; confidence comes from local validation only.

### Aspire / ServiceDefaults Revision — APPROVED (2026-03-25)

- **Artifact reviewed:** PR #46 / branch `squad/31-aspire-service-defaults-revision` at commit `2c20bae`.
- **Validation run:** `dotnet build .\SquadScout.slnx -nologo` passed, `dotnet test .\SquadScout.slnx -nologo --no-build` passed (55/55), and `dotnet run --project .\src\SquadScout.AppHost\SquadScout.AppHost.csproj --no-build` reached a healthy smoke start; broker `/health` returned `{"status":"ok"}` while AppHost was running.
- **Compatibility pattern confirmed:** `src\SquadScout.ServiceDefaults` multi-targeting `net8.0;net10.0` is the right seam for sharing OpenTelemetry/logging and `HttpClient` defaults across Functions, Broker, and MAUI without forcing Functions onto `net10.0`.
- **Critical orchestration seam confirmed:** `src\SquadScout.Broker\Program.cs` must only call `UseUrls(...)` when Aspire has not already injected `urls`; otherwise AppHost endpoint assignment is overridden and orchestration breaks.
- **Reviewer verdict:** **APPROVED** — the replacement revision closes the prior "no implementation / no handoff" rejection and is ready for merge, with the usual note that no GitHub checks are attached yet.

### PR #46 Merge Watch Handoff (2026-03-25T00:42:44Z)

- **Transition:** Link assumes merge-watch active state for PR #46 (Aspire / ServiceDefaults Revision).
- **Approval status:** Switch formal review APPROVED.
- **Local validation summary:** Build ✅ | Tests 55/55 ✅ | AppHost smoke ✅ | Broker /health ✅.
- **GitHub checks:** Absent; merge confidence based on local validation evidence.
- **Merge strategy:** Standard squash or commit per squad policy.
- **Link monitoring scope:** Main branch integration, post-merge CI/CD pipeline (if available), AppHost smoke validation post-merge.
- **Escalation:** Rollback assessment if post-merge AppHost smoke fails or GitHub checks regression occurs.

### Issue #13 Re-review — REJECTED (2026-03-25)

- **Artifact reviewed:** PR #44 / branch `squad/13-broker-session-start-stop-endpoints` at commit `b01f168`.
- **Validation run:** `dotnet build .\SquadScout.slnx -nologo` passed, focused `SessionRelayPipelineTests` passed (9/9), and full solution tests passed (60/60) in `D:\GitHub\SquadScout-13`.
- **Confirmed fix:** stop-related input rejection now returns structured `SessionControlException` contract with code `session_stop_in_progress`, and the new gated PTY test proves the successful stop/input overlap path deterministically.
- **Remaining blocker:** `InMemorySessionRelay.StopAsync(...)` still clears `_stopRequested` via `ResetStopRequest()` after `TerminateAsync()` failures without re-entering `StopInputGate`, so input can be admitted again after stop was already accepted if the PTY remains running.
- **Coverage gap that matters:** the new deterministic regression test only covers successful stop completion; no failing-stop / `session_stop_failed` overlap test guards the remaining race.
- **Recommended next reviser:** Seraph. Link authored the original rejected revision, Morpheus authored this rejected correction, so the next cycle should move to a third agent with lifecycle ownership.

### Issue #13 Final Re-Review — APPROVED (2026-03-25)

- **Artifact reviewed:** PR #44 / branch `squad/13-broker-session-start-stop-endpoints` at commit `27aa9e1`.
- **Validation run:** `dotnet build .\SquadScout.slnx -nologo`, focused `SessionRelayPipelineTests` (10/10), and full solution tests (61/61) all passed in `D:\GitHub\SquadScout-13`.
- **Race-condition read:** `InMemorySessionRelay.StopAsync(...)` now routes terminate-failure recovery through `ResetStopRequestAsync(...)`, which reacquires `StopInputGate` before clearing `_stopRequested`; `RelayInputAsync(...)` uses the same gate for admission, so accepted stop no longer reopens input on the failure path.
- **Coverage read:** `SessionRelayPipelineTests` now prove both the successful accepted-stop overlap and the deterministic failing-stop overlap, including the `session_stop_failed` path staying blocked behind the shared gate before input can resume.
- **Verdict:** **APPROVED** — reviewer blocker is closed. Remaining merge notes are operational only: GitHub has no configured checks on PR #44, and the PR currently reports `mergeable_state: dirty`, so merge should wait for branch reconciliation if needed.


### PR #47 Review Kickoff (2026-03-25T01:22:48Z)

- **Event:** PR #47 review activation with parallel Seraph merge-watch contingent on approval
- **Requested by:** Ryan Graham
- **Coordination:** Switch review → (on APPROVED) → Seraph merge-watch
- **Scope:** Code quality, test coverage, build validation, merge readiness assessment
- **Orchestration logs:** 2026-03-25T01-22-48Z-switch.md, 2026-03-25T01-22-48Z-seraph.md

### Issue #51 Review — APPROVED (2026-03-25T16:38:40Z)

**Artifact reviewed:** WebPubSubUpstreamAuthenticator implementation for issue #51 Web PubSub upstream authentication.

**Validation evidence:**
- Constant-time HMAC-SHA256 signature validation using CryptographicOperations.FixedTimeEquals
- Support for both ce-signature and WebHook-Signature headers (canonical and alias per Azure CloudEvents spec)
- Managed Identity (Easy Auth) path strictly validates WEBSITE_INSTANCE_ID presence to ensure Azure Functions host boundary
- Comprehensive test coverage: PubSubUpstreamHandlerTests 10/10 passing (valid signatures, missing/invalid signatures, untrusted identities, OPTIONS requests, malformed payloads)

**Security read:**
- Signature derivation correctly uses ce-connectionId per Web PubSub CloudEvents contract (not body-based custom scheme)
- Easy Auth principal trust is scoped to Azure Functions platform only; local/non-Easy-Auth envs fall back to shared access key validation
- Request authentication runs before JSON parsing, preventing forged envelopes from reaching broker

**Verdict:** **APPROVED** — implementation is production-ready. Ready for merge.

### Issue #15 MAUI Project & Session UX Polish — 2026-03-25T17:26:59Z

**Trinity Scope Completed:**
- Single-session handoff pattern (project picker → session transcript) now fully enforced via explicit ViewModel state visualization
- Loading/empty/stale-selection/retry states expressed as computed properties feeding XAML conditionals
- Active session gating prevents silent resume, provides resume-first affordances and warnings
- Shell navigation hardened to prevent invalid state transitions
- Coordination with Switch: acceptance bar moved to ViewModel seam; MobileShellScaffoldTests remains untouched

**Switch Scope Completed:**
- ProjectSelectionViewModelTests: loading, empty, invalid refresh, active session gating, start success
- ActiveSessionViewModelTests: no-session, pending broker, invalid refresh, reconnect failure, dev fallback
- Lightweight test doubles (IAppNavigator, MainThread shim) enable isolated unit testing without XAML compilation
- Full app test project passed; targeted tests passed

**Files Modified:**
- Mobile: ShellNavigator.cs, ActiveSessionViewModel.cs, ProjectSelectionViewModel.cs, ActiveSessionPage.xaml, ProjectSelectionPage.xaml
- Tests: ProjectSelectionViewModelTests.cs, ActiveSessionViewModelTests.cs, ViewModelTestDoubles.cs, test project config
- Docs: Trinity history, Switch history, skills

**Verification:** Full solution build ✅ | Full test suite (no-build) ✅ | Acceptance bar met at ViewModel seam ✅

**Status:** ✅ **COMPLETE** — Ready for WS-2 phase (session datapath integration with Link/Morpheus).

### Issue #17 Acceptance — Phase 1 Diagnostics Bar (2026-03-26)

- **Minimum bar locked:** Phase 1 diagnostics are only acceptable if the broker exports ordering context (`generation`, expected vs. received client sequence, last accepted client sequence, replay window/gap metadata) and the app keeps a bounded local replay/ordering trace that can be inspected offline.
- **Actual seam adopted:** `MessagingConnectionService.RecentTraffic` is now the local-analysis-friendly hook. It must retain replay-request / replay-response envelopes, stay capped by `RecentTrafficCapacity`, and redact payload strings / sensitive JSON members before they are stored for inspection.
- **Correlation rule:** Replay diagnostics must preserve `CorrelationId` across the request/response pair and set `CausationId` back to the replay request `MessageId`; without that chain, ordering failures are much harder to reconstruct after the fact.
- **Focused coverage added:** `BrokerPhase1DatapathGateTests` now proves the HTTP validation export carries generation + gap context, `InMemorySessionOrchestratorReplayTests` proves replay responses keep correlation/causation plus available-window metadata, and `PubSubConnectionServiceTests` proves the app traffic hook captures replay diagnostics, redacts secrets, and stays bounded.
- **Determinism note:** The token-refresh transport test was flaky because it released the controlled delay before the refresh loop had actually scheduled it. Waiting for `RequestedDelays.Count == 1` before release keeps the full app suite stable.
- **Validation:** Focused broker/app diagnostics runs passed, then `dotnet test .\SquadScout.slnx --no-restore --verbosity minimal` passed cleanly (122/122).

### Issue #17 Landing Team Coordination (2026-03-25T21:41:50Z)

- **Gate role:** Executed acceptance review on completed Issue #17 implementation (Neo's branch, all 126 tests passing).
- **Verdict:** ✅ **APPROVED FOR MERGE** — All Phase 1 diagnostics acceptance criteria verified green. No blocking gaps remain.
- **Verification summary:** Broker replay buffer publishes metadata ✅ | Client-side tracking complete ✅ | Generation reset boundary correct ✅ | Correlation IDs stable ✅ | Secret-safe redaction comprehensive ✅ | Test coverage 122/122 passing ✅.
- **Cross-team coordination:** Neo (landing execution), Link (diagnostics contract), Morpheus (hardening + security review), Scribe (decision consolidation). All sign-offs in place.
- **Next:** Orchestration log recorded (2026-03-25T21-41-50Z-switch.md). Session log: 2026-03-25T21-41-50Z-issue-17-landing.md. Ready to merge PR #57 on Lead approval.

