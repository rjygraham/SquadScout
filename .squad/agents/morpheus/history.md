# Morpheus History

## Day 1 Context

- User: Ryan Graham
- Project: A remote-control Copilot wrapper with a local broker and mobile client.
- Stack: .NET broker, .NET MAUI, Azure Web PubSub, Azure Function, Microsoft Entra, and Orleans under evaluation.
- Key concerns: authentication, replay correctness, reconnect reliability, and performance under intermittent connectivity.

## Learnings

### Cross-Cutting Workstream Decomposition (2026-03-24)

- **Five workstreams protect trust boundaries:** Replay/sequence (WS1), token validation (WS2), heartbeat/liveness (WS3), input sanitization (WS4), secrets/observability (WS5).
- **First validation slice is a local, replay-traced harness:** No Azure, Orleans, or MAUI needed to prove sequence correctness. This derisk the entire app-level replay strategy.
- **Heartbeat with signed nonce prevents spoofing:** Combining sequence acknowledgment with nonce-based heartbeat enables safe reconnect and liveness detection.
- **Single-user broker simplifies threat model:** No per-user isolation logic needed; trust boundary is Entra → Function → Broker, then Broker ↔ PTY (both local).
- **App-level sequencing is the critical path:** Transport layer (PubSub) is untrusted; gap detection and replay buffer must be enforced at app level, not delegate to broker tier.
- **Phase 2 Orleans atomicity is a constraint, not a feature:** Use grain turns to ensure replay buffer updates are atomic; do not rely on Orleans clustering or streams for transport.


### User Directives Accepted (2026-03-24)
 
- **App-level sequencing/replay via Orleans confirmed.** Morpheus co-owns WS-1 (sequence validation layer) with Switch. Orleans grain single-threaded execution model enforces atomic state transitions.
- **Foreground app confirmed with service option future.** Single-user constraints and low concurrent sessions lock Phase 1 threat model. Phase 4 may introduce service-mode deployment and multi-user isolation.
- **Status:** All 5 Morpheus workstreams integrated into unified decomposition. WS-1 (sequence/replay) is critical path blocker. Ready to proceed with Phase 1 execution.

### Message Envelope Contract Review (2026-03-25)

- **Replay-safe ordering should be scoped to replayable broker→client frames.** Heartbeats, cumulative acknowledgements, and input acceptance should stay out of the replay sequence unless the team explicitly wants them buffered and replayed.
- **Ordering identity must be explicit.** Use `sessionId` plus a restart/generation marker whenever ordered state can reset without issuing a brand-new session id; otherwise reconnect cannot distinguish resumed state from a fresh stream.
- **Replay responses must publish the available sequence window.** Clients need `availableFrom/availableTo` plus a gap flag or lost-range metadata so overflow becomes an explicit recovery path instead of silent transcript corruption.
- **Timestamps are diagnostic, not causal.** Replay and duplicate handling should key off server-assigned sequence and stable message identifiers, not client clocks or transport ordering.
- **Current draft blocker:** `src\SquadScout.Contracts\Messages\HeartbeatPayload.cs` duplicates top-level acknowledgement data, so the contract currently has two potential sources of truth for heartbeat/ack state.
- **Key review paths for issue #2:** `.squad/decisions.md`, `.squad/skills/broker-session-lifecycle/SKILL.md`, `src\SquadScout.Contracts\Messages\`, and `tests\SquadScout.Broker.Tests\MessageEnvelopeContractTests.cs`.

### Formal Review — Issue #2 / PR #36 (2026-03-25)

- **Validation confirmed locally:** `dotnet build .\SquadScout.slnx -nologo` and `dotnet test .\SquadScout.slnx -nologo --no-build` both passed on `squad/2-message-envelope-contract` at `b4efdab`.
- **Replay-safe contract gate:** A shared message envelope is still not merge-safe until it defines who owns `Sequence` for each direction and prevents client-authored frames from entering the broker replay ordering domain by ambiguity.
- **Ordering identity rule remains mandatory:** Replay metadata must cover overflow *and* ordered-state resets; if `sessionId` can survive a reset, the contract needs an explicit generation marker.
- **Resolved pattern worth preserving:** Keep cumulative acknowledgement only on the top-level envelope; heartbeat payloads should carry liveness metadata only.
- **Key review paths:** `src\SquadScout.Contracts\Messages\MessageEnvelope.cs`, `src\SquadScout.Contracts\Messages\ReplayResponsePayload.cs`, and `tests\SquadScout.Broker.Tests\MessageEnvelopeContractTests.cs`.

### Formal Review Outcome — Issue #2 / PR #36 (2026-03-25T18:43:27Z)

**REJECTED.** Build & test pass confirmed. Ack duplication and gap reporting fixed. Two critical blockers remain:

1. **Sequence ownership undefined:** Contract does not distinguish broker-owned replay frames from client-authored traffic; risks client sequences being treated as replay domain input.
2. **Replay reset boundary unsafe:** No generation/epoch marker; reconnecting client cannot distinguish resumed state from fresh stream after broker/PTY restart.

**Next revision ownership:** Link (Switch sits out correction cycle). Morpheus will re-review replay semantics before merge.

**Impact:** Blocks Phase 2 grain implementation. Message envelope is critical path for issue #3 and phase gates. Recommendation to preserve resolved patterns (single-source-of-truth ack, heartbeat liveness-only separation).

### Issue #2 Contract Batch Parallel Execution (2026-03-25)

**Morpheus outcome (parallel risk pass):**
- Completed comprehensive security & performance risk pass on Switch's in-progress contract draft
- Identified 5 critical blockers: sequence ownership undefined, duplicate ack state, missing replay metadata, no generation marker, liveness/recovery coupling
- Documented 8-point reliability/security checklist for signoff (invariants for server ordering domain, sequence strictness, idempotent acks, gap detection)
- Listed 7 ambiguities requiring resolution (sequence scope, state reset, heartbeat replayability, ack/heartbeat separation, replay transport, gap handling, duplicate dedup)
- **Recommendation:** Lock invariants before Issue #3 to prevent semantic divergence across broker, PubSub, and application layers
- Decision note merged into `.squad/decisions.md` under "Message Envelope Contract Implementation — 2026-03-25"
- Team history updated with cross-agent learnings

### Issue #3 Sequence Validator & Replay Buffer (2026-03-24)

- `src\SquadScout.Broker\Sessions\` now holds the in-memory reliability core: `SessionSequenceValidator`, `CircularReplayBuffer`, `SessionRuntimeState`, and `InMemorySessionOrchestrator` coordinate broker-owned sequencing, cumulative ack tracking, and generation resets.
- Replayable broker frames are limited to sequenced `Output` and `SessionLifecycle` messages; heartbeat and replay-control envelopes stay outside the circular buffer so liveness chatter cannot evict transcript data.
- Replay overflow behavior is explicit: responses always publish `availableFromSequence` / `availableToSequence`, set `gapDetected` when the requested cursor falls behind the buffer head, and paginate deterministically via `maximumMessages`.
- Ordered-state resets are generation-scoped: `ResetGenerationAsync` clears broker sequence, client sequence, ack state, and replay buffer; stale-generation replay requests return a reset boundary (current generation + available window) instead of cross-generation data.
- Trust-boundary guard added: broker replay/validation paths now reject envelopes whose `{ projectId, sessionId }` do not match the targeted session state.

### Switch Acceptance Bar — Issue #3 (2026-03-25)

- **Acceptance checklist:** 5 core areas (client monotonic, ack semantics, buffer determinism, replay/reconnect safety, input validation)
- **Verification commands:** Standardized test filters for validator, buffer, and orchestrator tests
- **High-risk modes identified:** Ack duplication, gap interaction, bounds handling, generation drift, pagination, trust boundary (project-id)
- **Current coverage:** 6 areas strong, 7 areas needed before signoff (dup-ack, gap-ack, future-gen, project-id, invalid bounds, oversized batch, multi-page)
- **Pattern worth preserving:** Replay buffer scope (broker-transcript only), explicit overflow window, generation reset boundary, trust checks

### Issue #3 Formal Review Outcome (2026-03-24T19:30:09Z)

**REJECTED by Switch.** Build passed, focused broker tests passed (12/12), full solution tests passed (20/20).

**Strengths confirmed:**
- Broker-owned monotonic sequencing with client-side validation
- Cumulative ack high-water tracking (monotonic + idempotent)
- 500-message circular replay model with explicit overflow/gap reporting
- Generation reset boundaries with empty replay + metadata response
- Heartbeat exclusion from replay storage
- Session-id trust-boundary rejection

**Blocking gaps (coverage-driven):**
1. No focused test for `SequenceValidationStatus.FutureGeneration` path; ack state preservation unproven
2. No separate trust-boundary test for project-id mismatch replay rejection (suite covers session-id only)

**Revision owner:** Link (Morpheus locked out per team protocol for rejection correction cycle)

**Impact:** Issue #3 remains unmerged; Phase 2 grain activation is blocked. No implementation bug found; coverage is the sole blocker. Morpheus is resting during Link's correction iteration.

### Issue #12 Token Validation & Session Claims Hardening (2026-03-25)

- **Easy Auth headers are only trustworthy inside the Azure Function boundary.** Local or non-Azure requests must not be allowed to self-assert `x-ms-client-principal*` headers; use the localhost development identity path instead.
- **Header/payload consistency is the tamper check worth preserving.** When Easy Auth provides both direct headers and the base64 principal payload, negotiate should reject mismatched principal/provider data instead of silently preferring one source.
- **Session scope belongs in the issued connection identity, not just the requested group name.** Encoding `{participantKind, projectId, sessionId, optional brokerId, principalId}` into the PubSub `userId` narrows replay/overreach blast radius and gives downstream components a stable authorization breadcrumb.
- **Client tokens should not self-select broker affinity before routing exists.** Rejecting `brokerId` on client negotiate requests prevents callers from minting narrower-but-unvetted subgroup identities ahead of issue #14.
- **Negotiate responses should stay minimal.** Echoing internal roles or Entra identity metadata back to the caller was unnecessary for current clients and widened the contract surface without adding security value.

### Issue #13 Revision — Stop/Input Lifecycle Hardening (2026-03-25)

- **Stop acceptance must share a critical section with PTY input admission.** A stop flag check outside the write-admission gate is not enough; once stop is accepted, no later input can be allowed to cross into the PTY write path.
- **Reuse lifecycle error codes when semantics are the same.** Rejecting input after stop acceptance should emit the existing `session_stop_in_progress` structured contract instead of inventing a second 409 code for the same transient condition.
- **Deterministic gated PTY doubles make race regressions reviewable.** Blocking `WriteAsync()` and `TerminateAsync()` with test-controlled gates proves serialization guarantees without relying on flaky task timing.
