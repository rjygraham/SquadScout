# Decisions

## 2026-03-24

- Accepted: This repository uses a persistent Squad team with Matrix-themed agent names.
- Accepted: The core product is a PTY-style wrapper for GitHub Copilot that supports remote operation from a .NET MAUI mobile app.
- Accepted: A local .NET broker host process is responsible for spawning the Copilot instance and forwarding messages to and from it.
- Accepted: Azure Web PubSub is the realtime transport between the broker and the mobile client.
- Accepted: The .NET MAUI app authenticates through Microsoft Entra, and an Azure Function uses managed identity to issue Azure Web PubSub tokens.
- Accepted: The broker must support configuring multiple local projects through a simple local web UI and persist project path configuration to storage.

## Architecture Plan Decisions — 2026-03-24

**From Neo's architecture plan (2026-03-24)**

### Technical Stack (Accepted)

- **Adopted:** Microsoft Orleans with constrained scope (session grains + project grains, SQLite persistence, no clustering/streams).
- **Adopted:** Pty.Net for PTY management (Microsoft's ConPTY wrapper, cross-platform).
- **Adopted:** Message replay via grain-held circular buffer (500-message default, gap detection on overflow).
- **Adopted:** Azure Function as token broker (Entra validation + managed identity for PubSub token minting).
- **Adopted:** Broker uses server-side PubSub SDK (connection string in user secrets).
- **Adopted:** PubSub groups per session (`session:{projectId}:{sessionId}` for message isolation).
- **Adopted:** Blazor Server for local web UI (co-hosted on broker's Kestrel, localhost-only).

### Implementation Phasing (Accepted)

- Phase 1 (Weeks 1–3): Core PTY bridge (Copilot PTY ↔ PubSub ↔ MAUI datapath).
- Phase 2 (Weeks 4–5): Orleans state + replay (durable session state, reconnect safety).
- Phase 3 (Weeks 6–7): Local web UI + multi-project (Blazor project config, multi-session routing).
- Phase 4 (Weeks 8–9): Polish + hardening (ANSI rendering, security review, observability).

### Assumptions (Accepted)

- Single-user: Broker serves one developer on their own machine (multi-user deferred).
- Message envelope with sequence numbers, heartbeats, and replay semantics as defined in §4.
- Session lifecycle: grain activation → PTY spawn; grain deactivation → PTY kill.

### Orleans SQLite Provider (Accepted — 2026-03-24)

- **Adopted:** `Microsoft.Orleans.Persistence.AdoNet` (10.0.x) + `Microsoft.Data.Sqlite` for grain storage.
- **Clustering:** `UseLocalhostClustering()` for in-memory membership (no clustering DB needed).
- **Schema bootstrap:** Run `Sqlite-Main.sql` + `Sqlite-Persistence.sql` on broker startup if tables absent.
- **Rationale:** First-party support, perfect fit for single-silo Phase 2 constraints, zero infrastructure, easy migration path to server DB later.
- **Risk:** GitHub issue dotnet/orleans#8187 (2022, fixed in Orleans 10 main). Early smoke test with NuGet versions recommended.

## User Directives — 2026-03-24

**From Ryan Graham (via Copilot) — Architecture & Product Direction**

- **Accepted (2026-03-24T16:21:26Z):** Use MudBlazor for the local project-configuration UI.
- **Accepted (2026-03-24T16:23:31Z):** For MAUI terminal rendering, use native controls with a chat-like interaction model instead of terminal-style rendering (SkiaSharp/xterm.js deferred).
- **Accepted (2026-03-24T16:27:03Z):** For Copilot spawn mode, start with direct spawn first; optional shell mode later.
- **Accepted (2026-03-24T16:28:01Z):** For message contract sharing, use a shared source project (SquadScout.Contracts) rather than NuGet package.
- **Accepted (2026-03-24T16:31:07Z):** Broker user model: start single-user, but design shared Azure infrastructure (Web PubSub, Azure Functions) to support multiple locally hosted brokers on different machines for different users.
- **Accepted (2026-03-24T16:32:46Z):** Broker hosting mode: start as foreground app; keep option to run as Windows Service / systemd later.
- **Accepted (2026-03-24T16:47:28Z):** For reliability, rely on app-level sequencing and replay rather than service-tier features. Use Orleans' single-threaded turn-based execution model to enforce atomic, ordered state transitions.
- **Accepted (2026-03-24T16:48:57Z):** Per-broker concurrency design: keep very low (single-user scope), but allow single user to run multiple concurrent sessions.
- **Accepted (2026-03-24T16:53:05Z):** MAUI app must support text-to-speech for incoming messages and speech-to-text for outgoing messages to enable on-the-go use (including while driving).

## Open Questions (Superseded by User Directives)

*Closed on 2026-03-24:*
- ~~Terminal rendering strategy~~ → Native MAUI chat UI (directive accepted)
- ~~Copilot spawn mode~~ → Direct spawn first (directive accepted)
- ~~Message contract sharing~~ → Shared source project (directive accepted)
- ~~Broker lifecycle~~ → Foreground app first (directive accepted)
- ~~Maximum concurrent sessions~~ → Low, single-user scope (directive accepted)
- ~~Multi-user support~~ → Deferred; design for scalability (directive accepted)

## Morpheus — Issue #3 Sequence Validator & Replay Buffer (2026-03-24)

**Context:** In-memory sequence validator and circular replay buffer define app-level reliability before transport integration.

**Decisions:**

- **Replay buffer scope:** Only sequenced broker transcript frames (`Output` and `SessionLifecycle`) enter the circular replay buffer. Heartbeats and replay-control envelopes remain outside the buffer so control chatter cannot evict transcript state.
- **Overflow behavior:** When a replay cursor falls behind the buffer head, the broker returns the overlapping available window, sets `gapDetected = true`, and publishes `availableFromSequence` / `availableToSequence` so the client can perform explicit recovery.
- **Generation reset boundary:** A replay request for an older generation returns a reset-boundary response for the current generation with window metadata and no cross-generation messages. This forces the client to detect ordered-state reset before applying new transcript data.
- **Trust-boundary enforcement:** Client envelopes must target the exact `{ projectId, sessionId }` bound to the session runtime state; mismatches are rejected before validation or replay logic mutates state.

## Switch — Issue #3 Acceptance Bar (2026-03-25)

**Owner:** Switch  
**Branch under review:** `squad/3-sequence-validator-replay-buffer`  
**Requested by:** Ryan Graham

**Acceptance checklist items:**

1. Client monotonic validation is explicit and tested (ClientSequence handling for first, contiguous, duplicate, gap, stale/future generation, non-positive values).
2. Ack semantics are cumulative, generation-scoped, and reviewer-readable (cannot exceed broker's last replayable, non-regressing, duplicate/gap tested, generation reset cleans state).
3. Circular replay buffer behavior is deterministic (500 default, only Output/SessionLifecycle, overflow eviction, boundary behavior tested).
4. Replay and reconnect semantics are safe (GapDetected, AvailableFromSequence/To, HasMore/IsComplete, stale-generation reset boundary, cross-session/project rejection, multi-page deterministic).
5. Input validation is covered (invalid replay bounds rejected/clamped, oversized batches explicit).

**High-risk failure modes:**
- Ack ambiguity on duplicate frames with changed ack
- Ack behavior during detected gaps
- Replay request bound handling
- Generation drift (stale and future)
- Window pagination after overflow
- Trust-boundary mismatch (project-id)

**Verification commands:**
```powershell
dotnet build .\SquadScout.slnx -nologo
dotnet test .\tests\SquadScout.Broker.Tests\SquadScout.Broker.Tests.csproj -nologo --filter "FullyQualifiedName~SessionSequenceValidatorTests|FullyQualifiedName~CircularReplayBufferTests|FullyQualifiedName~InMemorySessionOrchestratorReplayTests"
dotnet test .\SquadScout.slnx -nologo --no-build
```

**Current coverage (strong):** first client sequence, ack regression rejection, overflow gap signaling, reset-boundary replay, heartbeat exclusion, session-id mismatch rejection.

**Remaining coverage needed:** duplicate-with-changed-ack, gap-with-ack, future-generation, project-id mismatch, invalid bounds, oversized batch, multi-page pagination.

## Ordered Implementation Backlog — 2026-03-25

**From Neo's backlog synthesis (2026-03-25)**

### Backlog Overview (Proposal)

- **34 items** broken down across 4 phases (Days 1–9, Weeks 1–9)
- **Structure:** 5 groups — Immediate (Days 1–5), Near-term (Weeks 2–3), Phase 2 (Weeks 4–5), Phase 3 (Weeks 6–7), Phase 4 (Weeks 8–9)
- **First 3 items:** #1 Solution Scaffolding (Link), #2 Message Envelope Contract (Switch + Morpheus), #3 Sequence Validator & Replay Buffer (Morpheus + Switch)
- **Phase gates:** #16 (E2E integration test), #24 (Grain & reconnect test suite), #34 (Security hardening sweep)
- **Parallelism:** Items marked ‖ can run concurrently (e.g., #4 Mock PTY ‖ #5 CopilotPtyHost)

### Immediate Phase (#1–#8, Days 1–5)

- Proposal: Execute zero-dependency items: solution scaffold, message envelope, sequence validator, mock PTY, PTY host, relay logic, Azure Functions, input sanitizer
- Owners: Link, Switch, Morpheus, Seraph
- Dependencies: None — can start immediately
- Status: **Proposal — awaiting user acceptance**

### Near-term Phase (#9–#17, Weeks 2–3)

- Proposal: Complete Phase 1 datapath (MAUI shell, PubSub connection, Entra auth, session endpoints, MAUI UX, E2E test)
- Gate: #16 passes (Phase 1 integration test)
- Owners: Trinity (MAUI), Link (broker endpoints), Morpheus (token validation), Seraph (Functions), Switch (testing)
- Status: **Proposal — awaiting user acceptance**

### Phase 2 (#18–#24, Weeks 4–5)

- Proposal: Durable state via Orleans grains, grain migration, reconnect flow, heartbeat liveness, token refresh, grain test suite
- Gate: #24 passes (grain & reconnect test suite)
- Owners: Link (Orleans), Trinity (reconnect), Morpheus (heartbeat), Seraph (token refresh), Switch (tests)
- Status: **Proposal — awaiting user acceptance**

### Phase 3 (#25–#29, Weeks 6–7)

- Proposal: Rich UX — Blazor Server web UI, project config CRUD, TTS service, STT service, voice test harness
- Owners: Link (Blazor), Trinity (voice I/O), Switch (testing)
- Status: **Proposal — awaiting user acceptance**

### Phase 4 (#30–#34, Weeks 8–9)

- Proposal: Hardening & polish — multi-broker identity, structured logging, graceful shutdown, diagnostic harness, security sweep
- Gate: #34 passes (security hardening sweep)
- Owners: Seraph (multi-broker), Link (logging/shutdown), Switch (diagnostics), Morpheus (security)
- Status: **Proposal — awaiting user acceptance**

### Key Insights

- **Single highest-priority blocker:** Message envelope contract (#2) — everything depends on it
- **Parallelism pattern:** When specialist proposals overlap, assign one unified workstream with sub-deliverables per owner rather than parallel streams
- **Cross-cutting security:** Morpheus's security baseline (#8) runs in parallel with Phase 1 datapath (not deferred to Phase 4)
- **Dependency flattening:** Items #1–#8 have minimal cross-dependencies; can execute 8 items in parallel in Week 1
- **Integration gates enforce discipline:** Each phase must pass its gate test before proceeding

### Reference

- **Full backlog:** `.squad/decisions/inbox/neo-ordered-backlog.md` (34 items with done-when criteria, dependency graph)
- **Derived from:** Unified Workstream Decomposition (2026-03-24) + all accepted user directives

## Morpheus Formal Review — Issue #2 / PR #36 (2026-03-25)

**Verdict:** REJECTED

### Build & Test Status

- `dotnet build .\SquadScout.slnx -nologo` ✓ pass
- `dotnet test .\SquadScout.slnx -nologo --no-build` ✓ pass
- Commit: `b4efdab`
- Branch: `squad/2-message-envelope-contract`

### Resolved Issues from Prior Pass

- ✓ Heartbeat payloads no longer duplicate acknowledgement state
- ✓ Replay responses now advertise available replay window and explicit gap detection

### Remaining Critical Blockers

1. **Sequence ownership undefined**
   - `MessageEnvelope.cs` exposes one `Sequence` field for both `ClientToBroker` and `BrokerToClient` traffic
   - Contract does not clarify replay ordering scope: broker-owned only, direction-scoped, or shared
   - `MessageEnvelopeContractTests.cs` models `ClientToBroker` replay request with `Sequence = 120`, keeping ambiguity alive
   - **Risk:** Client-authored frames may be treated as part of broker replay stream
   - **Fix required:** Either reserve `Sequence` for broker-assigned replayable frames (make client sequencing separate/nullable), or add distinct client/server sequence fields with documented semantics

2. **Replay reset boundaries unsafe**
   - `MessageEnvelope.cs` and `ReplayResponsePayload.cs` have no generation/epoch marker
   - Replay responses expose overflow window metadata (`AvailableFromSequence`, `AvailableToSequence`, `GapDetected`) but cannot signal ordered-state reset after broker/PTY restart
   - **Risk:** Reconnecting client cannot distinguish resumed state from fresh stream
   - **Fix required:** Add session generation/epoch field to contract, or explicitly codify that ordered-state resets must mint brand-new `SessionId`

### Review Paths

- `src\SquadScout.Contracts\Messages\MessageEnvelope.cs`
- `src\SquadScout.Contracts\Messages\ReplayResponsePayload.cs`
- `tests\SquadScout.Broker.Tests\MessageEnvelopeContractTests.cs`

### Revision Ownership

- **Recommended next owner:** Link (Switch to sit out correction cycle)
- **Morpheus:** Will re-review replay semantics before merge approval

### Impact

- Blocks Phase 2 grain activation and replay buffer implementation
- Message envelope is critical path for issue #3 (Sequence Validator) and phase gates

## Message Envelope Contract Implementation — 2026-03-25

**From Morpheus & Switch parallel execution (2026-03-25)**

### Security & Performance Risk Pass (Morpheus)

**Identified replay-safety blockers:**

- **Sequence ownership undefined:** `Sequence` and `AcknowledgedSequence` appear on every envelope, but server authority is not established.
- **Duplicate ack state:** `HeartbeatPayload` carries `LastSequenceSeen` and `LastAcknowledgedSequence`, creating two possible sources of truth alongside top-level fields.
- **Missing replay metadata:** `ReplayResponsePayload` lacks `availableFromSequence`, `availableToSequence`, and gap/lost-range details, so overflow cannot be surfaced safely.
- **No generation marker:** No explicit contract rule that reconnect state reset issues a fresh `sessionId`, making replay scope ambiguous.
- **Liveness/recovery coupling:** Heartbeat conflates `ReplayRequested` flag with ack state without explicit boundary rules or challenge/proof fields for spoof-resistance.

**Morpheus checklist for signoff (8 critical invariants):**

1. Define server-assigned, per-session ordering domain for replayable broker → client frames
2. State whether ordering key is `{sessionId, sequence}` or `{sessionId, generation, sequence}`
3. Make sequence strictly increasing and gap-free on happy path; duplicates must be detectable
4. Give every envelope stable message id, contract version, project id, session id, message kind, direction, server timestamp (UTC), and correlation id
5. Keep timestamps diagnostic only; sequence wins for ordering
6. Make acknowledgements cumulative and idempotent (`ackUpToSequence` / highest contiguous seen)
7. Decide whether heartbeat is the ack carrier or whether ack has its own control frame
8. Define replay as explicit request/response contract with available range metadata and overflow/gap signaling

### Contract Implementation (Switch)

**Chosen shape:** `MessageEnvelope<TPayload>` in `src\SquadScout.Contracts\Messages\` with shared JSON settings in `SessionMessageSerializer.DefaultOptions` (camelCase + string-enum wire format).

**Key decisions:**

- Acknowledgement remains top-level `AcknowledgedSequence` (single source of truth)
- Heartbeat payloads carry only liveness metadata (`ReplayRequested`, `ExpectedIntervalSeconds`, `SenderInstanceId`) to avoid duplicate ack state
- Replay responses explicitly publish requested range and available window (`AvailableFromSequence`, `AvailableToSequence`, `GapDetected`) so reconnect overflow is explicit
- Backward compatibility rule for contract version 1: additive optional members and new message types allowed; renames, removals, or sequence/ack semantic changes require major version bump

**Status:** Implementation complete (commit b4efdab), draft PR #36 opened (closes #2), all tests passing. Awaiting Morpheus review feedback incorporation before final signoff.

## GitHub Issues Import — 2026-03-24

**From Neo's GitHub backlog export (2026-03-24)**

### Context

Ryan asked Neo to turn the full ordered local backlog into real GitHub issues in `rjygraham/SquadScout`, preserve execution order, add Squad routing labels, and avoid duplicating existing open issues.

### Team-Relevant Decisions

- The canonical detailed backlog artifact (`.squad/decisions/inbox/neo-ordered-backlog.md`) had already been deleted after Scribe merged the summary into `.squad/decisions.md`.
- To avoid blocking execution, the issue bodies were reconstructed from the merged backlog section in `.squad/decisions.md`, the ordered-backlog session log, `.squad/routing.md`, and the specialist agent histories.
- Order and gates were preserved explicitly in titles/bodies:
  - `Backlog #16` remains the **Phase 1 → Phase 2** gate.
  - `Backlog #24` remains the **Phase 2 → Phase 3** gate.
  - `Backlog #34` remains the **final hardening gate**.
- Routing labels were standardized for future pickup:
  - `squad`
  - `squad:link`, `squad:trinity`, `squad:seraph`, `squad:morpheus`, `squad:switch`, `squad:neo`
  - `phase:1`, `phase:2`, `phase:3`, `phase:4`

### Duplicate Check

- Existing open GitHub issues checked before import: **none**
- Duplicates skipped during import: **none**

### Mapping

| Backlog item | GitHub issue | Title |
| --- | --- | --- |
| #01 | #1 | Solution & Project Scaffolding |
| #02 | #2 | Message Envelope Contract |
| #03 | #3 | Sequence Validator & Circular Replay Buffer |
| #04 | #4 | Mock PTY Harness |
| #05 | #5 | CopilotPtyHost (Direct Spawn) |
| #06 | #6 | Broker Relay Pipeline |
| #07 | #7 | Azure Function Negotiate Endpoint |
| #08 | #8 | Input Sanitization & Secret-Safe Logging Baseline |
| #09 | #9 | MAUI App Shell Scaffolding |
| #10 | #10 | MAUI Session Transcript UI |
| #11 | #11 | PubSub Client Connection Service |
| #12 | #12 | Token Validation & Session Claims Hardening |
| #13 | #13 | Broker Session Start/Stop Endpoints |
| #14 | #14 | PubSub Session Routing & Group Membership |
| #15 | #15 | MAUI Project & Session UX Polish |
| #16 | #16 | End-to-End Phase 1 Datapath Gate |
| #17 | #17 | Phase 1 Session Telemetry & Replay Diagnostics |
| #18 | #18 | Orleans Silo Host & SQLite Bootstrap |
| #19 | #19 | Session Grain & Durable Replay State |

## User Directives — 2026-03-24T22:25:35Z

**From Ryan Graham (via Copilot)**

- **Accepted:** Ensure PRs are merged in a way that minimizes merge conflicts.

## PR Merge Watcher — PR #42 & #41 (2026-03-24–2026-03-25)

**Owner:** Morpheus & Seraph  
**Task:** Explicitly watch PR #41 and #42 for clean merge conditions

### PR #42 Status

**Title:** Implement issue #8 safety baseline  
**Issue Closed:** #8  
**Merge Timestamp:** 2026-03-24T22:28:52Z  
**Merge Commit:** 7d9c3c7  

**Merge Strategy:** Squash merge  
**Rationale:** Single logical unit, minimize main history clutter, reduce downstream merge conflict surface

**Verification:** ✅ Issue #8 auto-closed on merge; build and broker tests passed

### PR #41 Status

**Title:** Implement Azure Function negotiate endpoint  
**Issue Closed:** #7  
**Merge Timestamp:** 2026-03-24T18:30:09Z  
**Merge Commit:** 377581a  

**Merge Strategy:** Standard commit merge (preserve history)  
**Rationale:** Single logical commit, readable linear history, aligns with rebase-first conflict-minimization philosophy

**Verification:** ✅ Issue #7 auto-closed on merge; no follow-up fixes required

**Key Pattern Locked:** Session group naming `session:{projectId}:{sessionId}[:brokerId]`, managed-identity token flow, localhost dev fallback

## Wave 1 Launch Validation & Phase Routing (2026-03-25)

**Lead:** Neo  
**Status:** Approved for execution

### Issue #1 Reconciliation

All deliverables shipped to main (commit `228c0c1`):
- Multi-project solution structure (`.sln` + `.slnx`)
- MAUI app baseline (all platforms: iOS, macOS Catalyst, Android, Windows)
- Broker host skeleton with DI and configuration
- Contracts project with shared message types
- Azure Functions baseline with `NegotiateFunction` stub
- Test project scaffold (`SquadScout.Broker.Tests`)
- Directory build props & .NET 10 configuration

**Decision:** Close issue #1 with reference to shipped commit `228c0c1` and PR #35

### Wave 1 Branches Ready

| Issue | Branch | Owner | Status | Merge Base |
|-------|--------|-------|--------|-----------|
| #6 | `squad/6-broker-relay-pipeline` | Seraph | Ready | `6d2160f` |
| #7 | `squad/7-azure-function-negotiate-endpoint` | Trinity | Ready → Merged | `6d2160f` |
| #8 | `squad/8-input-sanitization-secret-safe-logging` | Morpheus | Ready → Merged | `6d2160f` |
| #9 | `squad/9-maui-app-shell-scaffolding` | Trinity | Ready → In Review (PR #43) | `6d2160f` |

**Execution Plan:** Phase 1B — Broker & Azure Integration (Issues #6–#9, parallel execution)

**Integration gate:** Issue #16 (E2E integration test) must pass before Phase 2 (Orleans grains & reconnect)

**Decision:** Approved. Launch wave 1 for parallel execution.
| #20 | #20 | Project Grain & State Migration Path |
| #21 | #21 | Reconnect & Replay Resume Flow |
| #22 | #22 | Heartbeat & Liveness Model |
| #23 | #23 | Token Refresh & Session Rejoin Flow |
| #24 | #24 | Grain & Reconnect Test Suite Gate |
| #25 | #25 | MudBlazor Local Admin UI Shell |
| #26 | #26 | Project Configuration CRUD & Persistence |
| #27 | #27 | MAUI Text-to-Speech Playback |
| #28 | #28 | MAUI Speech-to-Text Composer |
| #29 | #29 | Voice I/O Test Harness & Accessibility Pass |
| #30 | #30 | Multi-Broker Identity & Session Affinity |
| #31 | #31 | Structured Logging & Correlation IDs |
| #32 | #32 | Graceful Shutdown & Resume-Safe Lifecycle |
| #33 | #33 | Diagnostic Harness & Session Export Tooling |
| #34 | #34 | Security Hardening Sweep & Final Gate |

## Switch — Issue #4 Mock PTY Harness (2026-03-24)

**Owner:** Switch  
**Branch:** `squad/4-mock-pty-harness`  
**Requested by:** Ryan Graham

**Decision:** Adopt a two-layer PTY seam:

1. **Broker-facing host contract:** `src\SquadScout.Broker\Pty\IPtyHost.cs` starts PTY sessions from `PtySessionStartRequest`.
2. **Per-session PTY contract:** `src\SquadScout.Broker\Pty\IPtySession.cs` owns raw writes, event reads, termination, and lifecycle state.

The mock implementation (`MockPtyHost` / `MockPtySession`) stays **event oriented** and emits `PtySessionEvent` values (`Started`, `Output`, `Exited`) with deterministic logical-tick scheduling. It does **not** mint broker envelopes, sequence numbers, replay metadata, or transport concerns.

**Why:** Preserves Link's seam requirement that PTY simulation stay host-shaped and transport-free. Keeps sequencing/replay ownership in `src\SquadScout.Broker\Sessions\`. Gives issue #5 a drop-in contract for the real Copilot PTY host. Gives issue #6 a clean place to translate PTY events into broker envelopes and relay publication.

**Coupled follow-on choices:**
- `IRelayPublisher` now exposes `PublishEnvelopeAsync<TPayload>` so tests and later relay code can observe broker envelope publication without hiding sequencing inside ad hoc helpers.
- `SessionRuntimeState` updates `SessionDescriptor.State` when it records `SessionLifecyclePayload`, making running/stopped transitions visible through `ISessionOrchestrator.GetAsync`.
- Typed payloads now exist for `Input`, `Output`, and `SessionLifecycle` under `src\SquadScout.Contracts\Messages\`.

**Status:** Accepted (merged in #38).

## Switch — Issue #5 CopilotPtyHost Acceptance Bar (2026-03-24)

**Owner:** Switch  
**Branch:** `squad/5-copilot-pty-host`  
**Requested by:** Ryan Graham

**Decision:** Reviewer signoff for issue #5 should require a **direct-spawn-only** Copilot PTY host that preserves the existing PTY seam from issue #4:

1. `src\SquadScout.Broker\Pty\IPtyHost.cs` remains the broker-facing start contract.
2. `src\SquadScout.Broker\Pty\IPtySession.cs` remains the per-session contract for writes, event reads, state, termination, and async disposal.
3. The real host emits only `PtySessionEvent` values (`Started`, `Output`, `Exited`) so the broker/session layer keeps ownership of sequencing, replay, and relay publication.
4. Shell mode is **explicitly deferred** and should not appear in the issue #5 implementation or acceptance tests.

**Acceptance checklist:**
1. Direct spawn lifecycle is explicit (validation, launch, ready event)
2. Current seam stays intact (substitutable for MockPtyHost)
3. Output streaming is compatible (ordered Output events)
4. Startup failures are surfaced cleanly (exceptions not hangs)
5. Cancellation and teardown are safe (idempotent, no leaks)
6. Exit semantics are reviewer-readable (graceful, non-zero, forced)
7. No shell-path creep (tests and implementation direct-spawn only)

**Highest-risk failure modes to cover:**
- `Started` emitted before PTY/process is usable
- Startup cancellation leaves a child or PTY handle behind
- Startup failure swallowed or false `Started` event
- `TerminateAsync` races with natural exit (duplicate/inconsistent)
- Real PTY output chunking differs from mock expectations
- stderr/startup-banner noise dropped or mislabeled
- Immediate non-zero exit treated as success
- Shell invocation accidentally slips in

**Status:** Accepted (Link completed, Switch approved).

## Switch — Issue #5 CopilotPtyHost Review (2026-03-24)

**Reviewer:** Switch  
**Artifact:** `squad/5-copilot-pty-host` (current workspace state)  
**Scope:** `src\SquadScout.Broker\Pty\*`, `tests\SquadScout.Broker.Tests\CopilotPtyHostTests.cs`  

**Verdict:** APPROVED

The implementation fully satisfies the acceptance bar.

**Strengths:**
1. **Correct Abstraction:** `CopilotPtyHost` uses `Pty.Net` but hides it completely behind `IPtyHost`, preserving the test seam established in Issue #4.
2. **Robust Lifecycle:** `CopilotPtySession` handles the complex dance of process exit vs. output stream draining with appropriate timeouts (5s) and forced cleanup.
3. **Test Coverage:** Happy path confirmed, failure modes (missing binary, pre-start cancellation) tested, idempotency proven, integration envelope pump working.

**Constraints Verified:**
- No Replay Logic (PTY layer only emits events)
- Shell Mode deferred
- Cross-platform (Pty.Net wraps ConPTY/pseudo-terminal)

**Next Steps:** Merge to main. Unblocks Issue #6 Broker Relay Pipeline.

## Link — Issue #5 PTY Exit Semantics (2026-03-24)

**Owner:** Link  
**Branch:** `squad/5-copilot-pty-host`  
**Issue:** #5 CopilotPtyHost (Direct Spawn)

**Decision:** For the real PTY host, the broker-visible `Exited` event must be emitted **after** the PTY reader has drained buffered output (or after a bounded forced-cleanup timeout), not immediately when the child process reports exit.

**Why:** ConPTY/process exit can race ahead of the final transcript bytes reaching the broker reader. Publishing `Exited` too early lets the broker stop pumping before the last `Output` chunks arrive, truncating transcript state and replay data. The PTY seam still stays transport-agnostic: the session only emits `Started`, `Output`, and `Exited`; sequencing remains outside the PTY layer.

**Follow-on constraints:**
1. `TerminateAsync` remains idempotent and produces at most one terminal `Exited` event.
2. Forced broker termination reports `Exited(null)` only when termination was already requested when process exit was observed.
3. Natural exits preserve the observed process exit code even if cleanup/drain work continues afterward.
4. Shell mode stays deferred; this decision applies to the direct-spawn path only.

**Implementation verified in:** `CopilotPtySession.cs`, `CopilotPtyHost.cs`, `CopilotPtyHostTests.cs`

**Status:** Accepted (implemented and approved by Switch).

## Switch — Issue #9 MAUI App Shell Scaffolding Review (2026-03-25)

**Reviewer:** Switch  
**Artifact:** PR #43 `squad/9-maui-app-shell-scaffolding`  
**Requested by:** Ryan Graham

**Verdict:** APPROVED

### Validation

- ✅ `dotnet build .\SquadScout.slnx -nologo`
- ✅ `dotnet test .\SquadScout.slnx -nologo --no-build`
- ✅ `dotnet test .\SquadScout.slnx -nologo` (36/36 tests passing; SquadScout.App builds for Windows and Android during test run)

### Findings

- MAUI shell scaffolding meets product intent: project-selection and active-session routes, auth/messaging/project-catalog/session-lifecycle seams registered.
- Development config supports seeded projects and offline pending sessions (reviewable before broker datapath online).
- App-shell test project validates fallback/state logic without resizetizer pull into test assembly.
- **Regression clear:** No duplicate `appicon.svg` output regression; appicon/resizetizer test path now passes.

### Review Decision

Approved for merge. Deliverable complete: MAUI app shell ready for Phase 2 Orleans integration.

**Next:** Trinity monitors merge CI and post-merge build health. Ready to advance to next workstream.

## Trinity — Merge Watch Issue #9 (PR #43) (2026-03-25)

**Role:** Merge watcher  
**Artifact:** PR #43 `squad/9-maui-app-shell-scaffolding` → `main`

**Assignment:** Switch approved PR #43; Trinity monitors merge completion and validates main branch health.

**Watch scope:**
- Pre-merge CI validation
- Post-merge build: `dotnet build .\SquadScout.slnx -nologo`
- Post-merge tests: `dotnet test .\SquadScout.slnx -nologo`
- Regression monitoring: appicon/resizetizer, shell navigation

**Decision gate:** If post-merge CI or validation fails, escalate to Neo for rollback vs. revision decision. If all validation passes, record successful closure of issue #9 and advance team to next workstream (Phase 2 Orleans grains or queued backlog).

**Status:** Active — waiting for merge trigger.

## Link — Issue #13 Broker Session Start/Stop Endpoints (2026-03-25)

**Owner:** Link  
**Branch:** `squad/13-broker-session-start-stop-endpoints`  
**Commit:** bb59652  
**PR:** #44 (closes #13)  

### Decision

Expose broker session lifecycle as:
- **POST /api/sessions/start** — `StartSessionCommand` with `projectId`, `sessionId` (client-provided), request metadata
- **POST /api/sessions/{sessionId}/stop** — `StopSessionCommand` with required `projectId` repeat (explicit project binding confirmation)

Broker error codes:
- **404:** `project_not_found`, `session_not_found`
- **409:** `project_repository_root_missing`, `project_repository_root_not_found`, `session_project_mismatch`, `session_already_stopped`, `session_not_started`, `session_not_active`, `session_stop_in_progress`

### Rationale

- Repeating `projectId` on stop makes session-to-project binding explicit and prevents accidental cross-project teardown (important for future client integration).
- Failing bad start requests before session creation avoids orphaned pending sessions and gives callers cleaner remediation paths.
- Session lifecycle remains owned by the existing PTY event pump (`Exited` → `SessionLifecycle` envelope); no parallel stop-state machine.
- Actionable error codes enable future relay/auth work without endpoint shape changes.

### Validation

- ✅ Build: green
- ✅ Tests: 55/55 passing
- ✅ PR #44 open with handoff notes

### Follow-up

If mobile UX later needs a visible "stopping" phase, add that as a separate contract change rather than overloading the current `SessionState` enum.

**Status:** Handoff complete. Awaiting Switch formal review on PR #44.

## Switch — Issue #31 Review Gate: Aspire / ServiceDefaults (2026-03-25T00:08:17Z)

**Owner:** Switch  
**Artifact:** Seraph's Aspire / ServiceDefaults revision (issue #31)  
**Verdict:** REJECTED  

**Blocking Findings:**

1. **No PR exists** — No GitHub pull request was opened for Aspire / ServiceDefaults scope.
2. **No implementation** — Named worktree (`seraph/issue-31-aspire-service-defaults`) at `D:\GitHub\SquadScout\.worktrees\seraph-issue-31-aspire-service-defaults` contains no diff from `origin/main`, no `AppHost` project, no `ServiceDefaults` project.
3. **No Aspire wiring** — No `DistributedApplication`, `AddServiceDefaults()`, `UseServiceDefaults()`, or `MapDefaultEndpoints()` integration in Broker, Functions, or App hosts.
4. **No reviewable handoff** — No Seraph implementation notes or validation report found in squad records.

**Validation Performed:**
- Baseline build/test on Seraph's named worktree: 55/55 tests pass (no regression).
- Cross-project analysis: Broker (`Program.cs`), Functions (`Program.cs`), App (`MauiProgram.cs`) all use direct host registration with no shared OpenTelemetry or ServiceDefaults hooks.

**Merge-Risk Context:**
- Target scope spans three host models: MAUI (`net10.0` multi-target), Broker (`net10.0` web), Functions (`net8.0` isolated worker).
- Any acceptable revision must demonstrate explicit compatibility and cross-project integration strategy for Aspire before review can proceed.

**Revision Assignment:**
- **Next owner:** Link (local host / orchestration / solution-structure expertise by routing)
- **Rationale:** Link owns host-level changes and solution structure; best fit for introducing AppHost scaffolding and cross-project ServiceDefaults wiring.
- **Seraph lockout:** Blocked from next revision cycle for this artifact.
- **New worktree:** `D:\GitHub\SquadScout-31-link`
- **New branch:** `squad/31-aspire-service-defaults-revision`



## Decision: Issue #13 / PR #44 Rejection — Switch Review (2026-03-25)

**Reviewer:** Switch  
**Verdict:** REJECTED  
**Artifact:** PR #44 — \squad/13-broker-session-start-stop-endpoints\  

### Blocking Findings

1. **Stop/input handoff not serialized** — Lifecycle race condition persists. InMemorySessionRelay.cs:163-173 checks IsStopRequested once then hands off to AcceptClientMessageAsync(); InMemorySessionRelay.cs:107-121 sets stop flag but never coordinates with ClientMessageGate. Result: input can slip through after stop acceptance in the exact area this review was asked to harden.

2. **Stop-in-flight input rejection returns unstructured 409** — Inconsistent with established SessionControlException contract for other session-control errors. Bare InvalidOperationException thrown in InMemorySessionRelay.cs:164-166 maps to unstructured \{ message = ... }\ instead of structured payload with \code\, \sessionId\, \projectId\, \state\. Missing machine-readable error code and test coverage (concurrent stop/input overlap not covered).

### Resolution

- **Reassigned to:** Morpheus
- **Reason:** Link authored; rotating reviewer for correction cycle.
- **Lockout:** Link locked out for this revision on issue #13.
- **Branch:** \squad/13-broker-session-start-stop-endpoints\
- **Ready-for-approval:** (1) Serialize stop and input acceptance; (2) Return structured lifecycle conflict for stop-in-flight input rejection; (3) Add focused test coverage for concurrent stop/input scenarios.
