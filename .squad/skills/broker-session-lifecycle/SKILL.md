---
name: "broker-session-lifecycle"
description: "Patterns for managing PTY-hosted session lifecycle, routing, and state in a local broker"
domain: "architecture"
confidence: "medium"
source: "earned"
---

## Context

When a local broker hosts multiple PTY sessions for different projects and must route user input to the correct process and output to the correct remote client, session lifecycle and routing decisions significantly impact resilience and operational complexity.

## Patterns

**In-memory session registry with explicit lifecycle:**
- Maintain a registry keyed by `{projectId}:{sessionId}` mapping to session state (PTY host, input queue, last-seen sequence).
- Session states: `Starting` → `Running` → `Terminating` → `Dead`.
- Single-session per project for MVP; multi-session per project requires queue arbitration and is deferred.

**Explicit session lifecycle endpoints:**
- POST `/api/sessions/{projectId}/start` → generate sessionId, spawn PTY, enqueue in registry. Respond with sessionId immediately (don't wait for PTY ready signal).
- POST `/api/sessions/{projectId}/{sessionId}/terminate` → set state to `Terminating`, signal PTY to kill, drain input queue, remove from registry.
- GET `/api/sessions/{projectId}/{sessionId}/status` → return `{ state, lastSeenSequence, inputQueueDepth, ptyExitCode }` for client replay logic.

**Input queue backpressure:**
- Buffer user input in a bounded queue (propose 100 frames per session).
- If queue fills, drop oldest frame and emit `inputQueueOverflow` event to client (signal that some input was lost).
- Do NOT block the HTTP endpoint; respond immediately and signal overflow asynchronously.

**Message routing via PubSub groups:**
- One group per session: `session:{projectId}:{sessionId}`.
- Broker publishes all output (stdout, stderr, prompt markers) to that group only.
- MAUI client joins the group on session start; leaves on session end.
- Prevents cross-session message leakage and simplifies isolation.

**Trust-boundary failure coverage:**
- Test wrong `sessionId` and wrong `projectId` separately for replay and validation paths.
- These protect different isolation failures; proving one does not prove the other.

**Timeout-based cleanup:**
- If session has no activity (input or heartbeat response) for >5 minutes, auto-terminate (graceful kill) and emit `sessionTimeout` event.
- Prevents zombie sessions after MAUI crashes.
- Configurable per environment (shorter for development, longer for unattended broker).

**Replay cursor tracking:**
- Each session client tracks `lastSeenSequence` (highest app-level sequence number it has processed).
- On reconnect, client sends `{ sessionId, lastSeenSequence }` to broker.
- Broker looks up session grain/state, retrieves circular buffer from that sequence onward, and sends replay envelope: `{ type: "replay", messages: [...], fromSequence, toSequence }`.
- If `lastSeenSequence` is older than buffer head (overflow occurred), include `gapDetected: true` in replay envelope.

**State durability migration strategy (Phase 1 → Phase 2):**
- Phase 1: In-memory registry; state lost on broker restart (acceptable for development).
- Phase 2 planning: Migrate session state to Orleans grain storage without breaking running sessions.
- Propose feature flag or gradual cutover: spin up Orleans silo alongside in-memory registry, dual-write session state, then redirect clients to grain endpoints.
- Keep in-memory registry as fallback for grain failures (circuit breaker pattern).

## Anti-Patterns

- Do NOT store session state only in the PTY process object; that's unobservable and breaks reconnect logic.
- Do NOT route all input to a single queue; per-session queuing prevents one fast client from starving another.
- Do NOT assume PubSub group membership is instant; acknowledge group join in the session status response.
- Do NOT auto-terminate sessions on single-frame input timeout; use cumulative idle timeout (no activity for N minutes).
- Do NOT leak session state across projects; ensure project isolation at routing layer (reject `{wrongProjectId}:{sessionId}` combos).

## Examples

- User starts session on Project A: `POST /api/sessions/projA/start` → `{ sessionId: "sess-001" }`. Broker spawns Copilot, creates `SessionState { projectId: "projA", sessionId: "sess-001", ptyHost: ..., inputQueue: [], lastSeenSequence: 0 }`, stores in registry.
- User sends input: `POST /api/sessions/projA/sess-001/input { text: "ls -la" }` → broker enqueues, PTY consumes, outputs "total 42\n...", broker publishes to PubSub group `session:projA:sess-001` with `{ sequence: 1, type: "stdout", text: "total 42\n..." }`.
- MAUI disconnects, reconnects 30s later with `{ sessionId: "sess-001", lastSeenSequence: 5 }`. Broker checks grain/registry, finds messages 6-15 in circular buffer, sends `{ type: "replay", messages: [...], fromSequence: 6, toSequence: 15, gapDetected: false }`.
- Session idle 5+ minutes: broker emits `{ type: "sessionTimeout", sessionId: "sess-001" }` to PubSub group, terminates PTY, removes from registry. MAUI detects and offers reconnect.

## References

- **Related skill:** `pty-broker-architecture` (session grain pattern, replay buffer, circular buffer semantics).
- **Related decision:** Architecture Plan Decisions § Implementation Phasing (Phase 1 in-memory, Phase 2 Orleans).

