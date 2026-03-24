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

### Morpheus Issue #3 Delivery (2026-03-24)

- **Branch:** `squad/3-sequence-validator-replay-buffer` (commit `8068e81`)
- **Scope:** In-memory reliability core: monotonic client validation, 500-message replay buffer, overflow/gap reporting, generation reset boundaries, trust-boundary checks
- **Implementation:** Broker session state + replay orchestrator with focused test suite
- **Decision gates merged:** Replay buffer scope, overflow behavior, generation reset boundary, trust-boundary enforcement all documented in `.squad/decisions.md`
- **Next:** Incorporate Switch's remaining high-risk coverage before signoff; merge to unblock Phase 2 grain implementation



