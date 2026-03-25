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

### 2026-03-25: Aspire orchestration + ServiceDefaults groundwork

- Aligned the repo's new Aspire AppHost and ServiceDefaults rollout with Seraph-owned startup concerns after the shared Aspire scaffolding landed on main.
- Documented why the shared defaults project stays multi-targeted for net8.0 and net10.0 so the Azure Functions worker can opt into AddServiceDefaults() while the MAUI app can initialize OpenTelemetry via the MAUI-specific startup hook.
- Preserved broker compatibility by surfacing the effective ASP.NET Core listen URL in the root status payload while still letting standalone runs honor Broker:ListenUrl.
- Updated the MAUI app to create broker clients through an Aspire-configured HttpClient factory seam so resilience and future service discovery can flow into mobile-to-broker calls without breaking offline fallbacks.
