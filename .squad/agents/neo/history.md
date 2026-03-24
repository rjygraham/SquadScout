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

### 2026-07-14 — Orleans SQLite Provider Research

- **Recommendation (proposed):** Use `Microsoft.Orleans.Persistence.AdoNet` (10.0.x) + `Microsoft.Data.Sqlite` for grain storage. Pair with `UseLocalhostClustering()`. No clustering or reminders DB needed.
- **Key finding:** Orleans 10 ships official `Sqlite-Persistence.sql` DDL scripts and registers `Microsoft.Data.Sqlite` in `DbConnectionFactory.cs`. The Orleans test suite (`SqlitePersistenceGrainStorageFixture.cs`) exercises this path.
- **Gap:** No official `Sqlite-Clustering.sql` or `Sqlite-Reminders.sql` scripts exist. Irrelevant for Phase 2 (single silo, grain timers not reminders).
- **Risk:** GitHub issue dotnet/orleans#8187 (2022, still open) flagged invariant registration bugs in Orleans 7. Source inspection confirms fixed on main for Orleans 10. Smoke test recommended.
- **Proposal file:** `.squad/decisions/inbox/neo-orleans-sqlite-provider.md`
- **User preference confirmed:** Ryan wants decisive calls, not option menus.

