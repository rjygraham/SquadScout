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

