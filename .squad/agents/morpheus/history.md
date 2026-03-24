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
