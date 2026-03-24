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

## Open Questions

- Terminal rendering strategy in MAUI (SkiaSharp vs. xterm.js via WebView)?
- Copilot spawn mode (direct vs. inside shell)?
- Message contract sharing (shared project vs. NuGet package)?
- Broker lifecycle (foreground app vs. Windows Service / systemd)?
- Maximum concurrent sessions per broker (benchmarking needed).
- Broker as service vs. foreground app (Phase 4 decision).

