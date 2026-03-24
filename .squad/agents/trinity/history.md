# Trinity History

## Day 1 Context

- User: Ryan Graham
- Project: Remote Copilot control from a .NET MAUI mobile app.
- Stack: .NET MAUI on .NET 10 communicating through Azure Web PubSub to a local .NET broker.
- Key concerns: intuitive mobile controls, session initiation, reconnect behavior, and project selection across multiple local repos.

## Learnings

- **Workstream Decomposition Pattern:** Mobile client work decomposes cleanly into 6 phases: infrastructure setup → datapath → session UX → hands-free RX → hands-free TX → resilience. This ordering respects broker dependencies and allows parallel execution of WS-1/3 while WS-2 unblocks audio features.

- **First Slice Strategy:** The MVP slice (WS-1/2/3 integration test) does not require Orleans, replay, or Entra—only a bare PubSub connection and hardcoded test messages. This validates the datapath in days, not weeks, before audio/reconnect complexity.

- **Constraint Recognition:** Single-user, single-session-at-a-time model means MAUI navigation is project selection → session (not sidebar multi-tab). Session persistence must survive app restart but not broker crash (Phase 1 assumption).

- **Dependency Management:** Most mobile blocks depend on Switch (message envelope) and Morpheus (session lifecycle). Early alignment with these teams on contracts (sequence numbering, replay API shape) is critical to unblock WS-2 on schedule.

- **Audio as Differentiator:** TTS + STT are full workstreams (WS-4/5), not afterthoughts. They require platform-specific testing (iOS/Android microphone/speaker perms) and deserve dedicated effort and design review for UX (record button prominence, confidence feedback).

### User Directives Accepted (2026-03-24)

- **Native chat UX confirmed.** No terminal-style rendering; SkiaSharp/xterm.js deferred indefinitely.
- **TTS/STT mandated for Phase 3.** On-the-go use case (including driving) requires accessibility; not optional.
- **Status:** WS-4 (TTS incoming) and WS-5 (STT outgoing) promoted to Phase 3 deliverables. Trinity ready to scaffold MAUI project and begin WS-4.

