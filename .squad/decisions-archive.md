# Decisions Archive

Old decisions (older than 30 days) are archived here for historical reference. Current working decisions remain in `decisions.md`.

---

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
- Phase 2: Orleans state + replay + persistence.
- Phase 3: Web UI + multi-project scope.
- Phase 4: Polish + hardening (graceful shutdown, reminders, observability).

---

**Note:** Newer decisions are in `decisions.md`.
