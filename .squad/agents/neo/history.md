# Neo History

## Day 1 Context

- User: Ryan Graham
- Project: PTY-style wrapper for GitHub Copilot with remote control from a .NET MAUI mobile app.
- Stack: .NET, .NET MAUI (.NET 10), Azure Web PubSub, Azure Functions, Microsoft Entra, and possible Orleans co-hosting.
- Key concerns: local broker orchestration, reconnect-safe messaging, session start commands, and multi-project configuration through a local web UI.

## Learnings

### 2025-07-18 — Architecture Plan Delivered

- **Orleans decision:** Adopt with constrained scope (session grains + project grains + SQLite persistence, single silo, no clustering/streams). Introduced Phase 2 to avoid coupling early PTY debugging to grain behavior.
- **PTY library:** Pty.Net (Microsoft ConPTY wrapper) chosen over raw Process.Start for proper terminal emulation.
- **Message replay:** Grain-held circular buffer (500 messages) with sequence numbers. Gap detection on overflow. Belt-and-suspenders with PubSub's own acks.
- **Auth flow:** MAUI → MSAL (Entra PKCE) → Azure Function (token validation + managed identity) → Web PubSub token. Broker uses server-side SDK with connection string in user secrets.
- **Local UI:** Blazor Server on Kestrel, localhost-only bind. Direct grain access from server-side Blazor circuits.
- **Phasing:** 4 phases — (1) Core PTY bridge, (2) Orleans state + replay, (3) Web UI + multi-project, (4) Polish + hardening.
- **Key file:** `.squad/decisions/inbox/neo-architecture-plan.md` — full architecture plan.
- **User preference:** Ryan wants specific recommendations, not option menus. Decisive architecture calls with rationale.
- **Single-user assumption:** Broker serves one developer on their machine. Multi-user is an open question.

### 2026-03-24 — Architecture Plan Merged to Decisions

- **Status:** Proposed decisions have been merged from inbox to `.squad/decisions.md`.
- **Process:** Scribe merged inbox entry, deleted file, updated team memory.
- **Next steps:** Team to review proposed decisions; Lead to initiate Phase 1 implementation agents (Link, Seraph, Trinity).
- **Key open questions:** Terminal rendering (SkiaSharp vs. xterm.js), Copilot spawn mode, message contract sharing, Orleans SQLite provider choice.

### 2026-03-24 — Orleans SQLite Provider Decision (Accepted)

- **Recommendation (accepted):** Use `Microsoft.Orleans.Persistence.AdoNet` (10.0.x) + `Microsoft.Data.Sqlite` for grain storage. Pair with `UseLocalhostClustering()`. No clustering or reminders DB needed.
- **Key finding:** Orleans 10 ships official `Sqlite-Persistence.sql` DDL scripts and registers `Microsoft.Data.Sqlite` in `DbConnectionFactory.cs`. The Orleans test suite (`SqlitePersistenceGrainStorageFixture.cs`) exercises this path.
- **Gap:** No official `Sqlite-Clustering.sql` or `Sqlite-Reminders.sql` scripts exist. Irrelevant for Phase 2 (single silo, grain timers not reminders).
- **Risk:** GitHub issue dotnet/orleans#8187 (2022, still open) flagged invariant registration bugs in Orleans 7. Source inspection confirms fixed on main for Orleans 10. Smoke test recommended.
- **Status:** Merged to `.squad/decisions.md`. Ready for Phase 2 implementation team (Link, Seraph, Trinity).

### 2026-03-24 — Unified Workstream Decomposition Delivered

- **Synthesis:** Merged 5 specialist proposals (Link ×5 WS, Trinity ×6, Seraph ×6, Morpheus ×5, Switch ×6) into 14 unified workstreams across 4 phases.
- **First executable slice:** Days 1–5, zero external dependencies — lock message contract, build mock PTY harness, prove sequence/replay correctness locally, stub MAUI receives messages. Owned by Switch + Morpheus + Link + Trinity.
- **Critical path identified:** WS-1 (contract) → WS-2 (PTY bridge) → WS-5 (E2E routing) → WS-7 (Orleans) → WS-8 (reconnect).
- **Key integration insight:** The message envelope contract is the single highest-priority blocker — everything depends on it. Switch and Morpheus co-own it, not any single domain.
- **Cross-cutting security:** Morpheus's security baseline (WS-6) runs in parallel with Phase 1 datapath work, not deferred to Phase 4. Input sanitization and no-secret logging are Day 1 concerns.
- **Voice I/O placed in Phase 3:** TTS/STT doesn't depend on Orleans, so it can start once the basic datapath works. Trinity owns both, with Switch providing test harness.
- **Decomposition pattern:** When specialist proposals overlap (e.g., reconnect touched by Trinity, Morpheus, and Seraph), assign one unified workstream with sub-deliverables per owner rather than creating parallel streams.
- **Key file:** `.squad/decisions/inbox/neo-workstream-decomposition.md`.

### 2026-03-24 — User Directives & Inbox Consolidation (Accepted)

- **Accepted (all 9):** User directives on MudBlazor, native MAUI chat UX, direct Copilot spawn, shared source contract, single-user+multi-machine design, foreground app, app-level sequencing/replay via Orleans, low concurrent sessions, TTS/STT for accessibility.
- **Process:** Scribe merged 9 decision inbox entries into new "User Directives" section in decisions.md. Archived 6 specialist workstream proposals. Updated team agent histories.
- **Impact on decomposition:** Decomposition already incorporates all user directives; no scope changes needed. Ready for Phase 1 team execution.
- **Status:** All open questions from architecture plan now closed by user directives. Proceeding to implementation.

### 2026-03-25 — Ordered Implementation Backlog Delivered

- **Output:** 34-item ordered backlog across 5 groups (Immediate / Near-term / Phase 2 / Phase 3 / Phase 4).
- **First 3 to start:** #1 Solution Scaffolding (Link), #2 Message Envelope Contract (Switch + Morpheus), #3 Sequence Validator + Replay Buffer (Morpheus + Switch).
- **Phase gates:** #16 (E2E integration test) gates Phase 1→2. #24 (grain & reconnect tests) gates Phase 2→3. #34 (security sweep) is the final gate.
- **Decomposition pattern:** Workstreams (WS-1 through WS-14) were broken into 2–3 backlog items each, with explicit dependencies and done-when criteria. Items #1–#8 are granular enough to start immediately.
- **Parallelism preserved:** Items marked ‖ can run concurrently (e.g., #4 Mock PTY ‖ #5 CopilotPtyHost, #7 Functions ‖ #5 PTY, #8 Security ‖ #6 Relay).
- **Key file:** `.squad/decisions/inbox/neo-ordered-backlog.md`.

### 2026-03-24T17:11:22Z — Ordered Implementation Backlog Merged to Decisions
 
- **Process:** Scribe merged backlog entry from inbox to `.squad/decisions.md` under "Ordered Implementation Backlog — 2026-03-25" section. Deleted inbox file. Created orchestration log (2026-03-24T17-11-22Z-neo.md) and session log (2026-03-24T17-11-22Z-ordered-backlog.md).
- **Status:** Backlog now **Proposal** in shared decisions.md, awaiting user acceptance before team execution.
- **Next step:** Lead to accept backlog and dispatch Link, Switch, Morpheus for #1–#3 kickoff.

### 2026-03-24 — Ordered Backlog Published to GitHub Issues

- **Repo target:** `rjygraham/SquadScout`.
- **Issue import result:** Created the full ordered backlog as GitHub issues **#1–#34** with a direct backlog-to-issue mapping and no open-issue duplicates to skip.
- **Labeling pattern:** Added reusable routing labels `squad:{member}` and execution labels `phase:{1..4}`; every backlog issue carries `squad`, one owner label, and one phase label.
- **Recovery decision:** The detailed `neo-ordered-backlog.md` artifact had already been deleted after merge, so the issue bodies were reconstructed from `.squad/decisions.md`, the ordered-backlog session log, routing metadata, and agent histories while preserving the original order, dependencies, and phase gates (#16, #24, #34).
- **Key files:** `.squad/decisions.md`, `.squad/decisions/inbox/neo-github-issues.md`, `.squad/skills/backlog-to-github-issues/SKILL.md`.

### 2026-03-24T17:31:17Z — Scribe Processing Complete

- **Scribe task:** Merged GitHub issues import decision from inbox to `.squad/decisions.md`.
- **Team histories updated:** Neo, Link, Trinity, Switch all received GitHub import context.
- **Logs created:** Orchestration log (2026-03-24T17-31-17Z-scribe.md) and session log (2026-03-24T17-31-17Z-github-issue-import.md).
- **Git commit:** `.squad/` directory staged and committed.
- **Status:** All scribe tasks complete. Team has full visibility into GitHub issues import and label routing pattern.

