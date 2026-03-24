# Morpheus History

## Day 1 Context

- User: Ryan Graham
- Project: A remote-control Copilot wrapper with a local broker and mobile client.
- Stack: .NET broker, .NET MAUI, Azure Web PubSub, Azure Function, Microsoft Entra, and Orleans under evaluation.
- Key concerns: authentication, replay correctness, reconnect reliability, and performance under intermittent connectivity.

## Learnings

### Cross-Cutting Workstream Decomposition (2026-03-24)

- **Five workstreams protect trust boundaries:** Replay/sequence (WS1), token validation (WS2), heartbeat/liveness (WS3), input sanitization (WS4), secrets/observability (WS5).
- **First validation slice is a local, replay-traced harness:** No Azure, Orleans, or MAUI needed to prove sequence correctness. This derisk the entire app-level replay strategy.
- **Heartbeat with signed nonce prevents spoofing:** Combining sequence acknowledgment with nonce-based heartbeat enables safe reconnect and liveness detection.
- **Single-user broker simplifies threat model:** No per-user isolation logic needed; trust boundary is Entra → Function → Broker, then Broker ↔ PTY (both local).
- **App-level sequencing is the critical path:** Transport layer (PubSub) is untrusted; gap detection and replay buffer must be enforced at app level, not delegate to broker tier.
- **Phase 2 Orleans atomicity is a constraint, not a feature:** Use grain turns to ensure replay buffer updates are atomic; do not rely on Orleans clustering or streams for transport.


### User Directives Accepted (2026-03-24)

- **App-level sequencing/replay via Orleans confirmed.** Morpheus co-owns WS-1 (sequence validation layer) with Switch. Orleans grain single-threaded execution model enforces atomic state transitions.
- **Foreground app confirmed with service option future.** Single-user constraints and low concurrent sessions lock Phase 1 threat model. Phase 4 may introduce service-mode deployment and multi-user isolation.
- **Status:** All 5 Morpheus workstreams integrated into unified decomposition. WS-1 (sequence/replay) is critical path blocker. Ready to proceed with Phase 1 execution.
