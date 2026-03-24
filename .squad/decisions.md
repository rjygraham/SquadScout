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

