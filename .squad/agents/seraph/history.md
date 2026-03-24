# Seraph History

## Day 1 Context

- User: Ryan Graham
- Project: Mobile clients connect through Azure Web PubSub to a local Copilot broker.
- Stack: Microsoft Entra, Azure Function with managed identity, Azure Web PubSub, and a .NET-based local broker.
- Key concerns: secure token issuance, authenticated mobile access, and reconnect-safe cloud contracts.

## Learnings

### 2026-03-24: Cloud/Auth Domain Decomposition

**Workstream Structure:**
- Seraph owns 6 workstreams: (1) Entra auth flow, (2) Function integration, (3) PubSub routing, (4) token refresh/reconnect, (5) multi-broker affinity, (6) observability.
- Each workstream is 1–3 weeks; most overlap; WS-1 and WS-2 unblock everything else.
- MVP slice is single session, local broker, cloud token path: Entra → Function → PubSub, broker joins group, MAUI joins group, multicast proves E2E.

**Key Constraints Locked:**
- Token minting via Function managed identity (no connection strings in code).
- Session groups named `session:{projectId}:{sessionId}[:brokerId]` (enforced in Function).
- No distributed session cache Phase 1/2; broker is source of truth for session state.
- Token TTL 1 hour; refresh proactive at 50-min mark.
- App-level reliability (sequence numbers, replay buffer in grain) — Web PubSub transport is best-effort.

**Dependencies Identified:**
- **Tight:** Link must deliver Entra token; Trinity must call `/negotiate`; Morpheus must provision infrastructure.
- **Loose:** Switch can mock Function offline; Neo's grain lifecycle aligns with group lifecycle.

**Rollout Risk:**
- Phase 1 → Phase 2: Orleans grain lifecycle must not break PubSub group consistency (grain activation ↔ group join, deactivation ↔ group leave).


### User Directives Accepted (2026-03-24)

- **Single-user + multi-machine design confirmed.** Design local broker scaffolding to support multiple developers with separate Azure credentials; no shared multi-user backend until post-MVP.
- **App-level sequencing/replay via Orleans confirmed** as source of truth, not Web PubSub features. Service-tier reliability features not required; Orleans grain single-threaded execution enforces atomicity.
- **Status:** All 6 Seraph workstreams aligned to Neo's unified decomposition. WS-3 (PubSub routing) and WS-4 (token refresh/reconnect) priority path locked. Ready for Phase 1 execution.

### 2026-03-25: Merge Watcher — PR #41 & #42 Monitoring

**Task:** Explicitly watch PR #41 and #42, merge only when clean.

**PR #42 Status:**
- Title: Implement issue #8 safety baseline
- State: **MERGED** (2026-03-24T22:28:52Z, beat #41 by ~2 minutes)
- Merge commit: User (`rjygraham`)
- Issue #8: Closed
- Checks: heartbeat SUCCESS ✓

**PR #41 Status (Seraph action):**
- Title: Implement Azure Function negotiate endpoint
- State: **OPEN** → **MERGED** (2026-03-24T18:30:09Z)
- Merge strategy: Create commit merge (single logical commit preserved)
- Pre-merge checklist:
  - Merge state: CLEAN ✓
  - Reviews: 0 (none blocking) ✓
  - Checks: 0 (no failures) ✓
  - Comments: 0 (no discussion) ✓
  - Conflicts: None ✓
- Key artifact: `src/SquadScout.Cloud/NegotiateFunction.cs` — Azure Function trusted boundary (Easy Auth, managed-identity token minting, session groups, localhost fallback)
- Issue #7: Auto-closed by PR body

**Outcome:**
- Both PRs now merged to main
- WS-2 (Function integration) trusted boundary tier complete
- WS-3 (PubSub routing) unblocked
- Merge conflict minimized via early merge before dependent work accumulates

### Issue #9 Handoff — Trinity Implementation Complete (2026-03-24T23:39:21Z)

**Status:** MAUI App Shell Scaffolding complete; PR #43 in review (awaiting Switch gate)

**Context:** Trinity completed issue #9 MAUI app shell scaffolding with cross-platform support and chat-like terminal UI. Build green, 36 tests pass. PR #43 opened; Switch begins formal review.

**Impact on Seraph Workstreams:**
- WS-3 (PubSub routing): Trinity's MAUI client integration now available to pair with Link's relay pipeline (issue #6).
- WS-4 (token refresh/reconnect): MAUI session bindings ready to receive negotiated tokens from NegotiateFunction (PR #41 already merged).
- **No blocking dependencies:** Trinity's implementation uses existing shared Contracts; no new contract changes on issue #9 critical path.

**Next:** Awaiting Switch approval. Once PR #43 merges, MAUI client tier can integrate with broker relay pipeline (issue #6, Link/Seraph parallel work).
