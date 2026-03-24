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

