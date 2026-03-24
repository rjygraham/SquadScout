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
