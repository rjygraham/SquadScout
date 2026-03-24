---
name: "pty-broker-architecture"
description: "Patterns for building a PTY-hosting broker with Orleans grains and cloud relay"
domain: "architecture"
confidence: "medium"
source: "earned"
---

## Context

When building a local process that hosts pseudo-terminal child processes and bridges their I/O to a remote client through a cloud relay (e.g., Azure Web PubSub), these patterns keep the design clean and resilient.

## Patterns

- **Grain-per-session:** Use Orleans virtual actors keyed by `{projectId}:{sessionId}` to own PTY process lifecycle, buffer messages, and handle replay. Grain deactivation = session cleanup.
- **Circular replay buffer in grain state:** Hold the last N messages in grain memory. On reconnect, replay from the client's `lastSeenSequence`. Signal `gapDetected` if the buffer has rolled over.
- **Sequence numbers are app-level:** Even when the transport (PubSub) has its own ack mechanism, maintain app-level monotonic sequence numbers per session for replay correctness.
- **Token broker pattern:** Use an Azure Function with managed identity as the token broker between Entra-authenticated clients and Azure Web PubSub. Never expose PubSub connection strings to mobile clients.
- **Localhost-only UI:** When the broker has a config UI, bind Kestrel to `127.0.0.1` only. Use Blazor Server for direct grain access without an API layer.
- **Phase Orleans in after I/O works:** Prove the raw PTY ↔ relay ↔ client datapath first with in-memory state. Add Orleans in a follow-up phase to avoid coupling I/O debugging to grain behavior.
- **PubSub groups for isolation:** One group per session prevents cross-session message leakage and simplifies routing.
- **Drain output before exit event:** When the PTY process exits, keep reading until the PTY stream reaches EOF (or a bounded timeout) before publishing the broker-visible exit event. Otherwise the last buffered Copilot output can be lost even though the process already reported an exit code.

## Examples

- Session grain spawns PTY in `StartAsync`, kills it in `TerminateAsync` and on grain deactivation.
- Broker heartbeat: grain timer emits `Heartbeat` every 15s; client detects liveness loss after 3 missed beats.
- Project config as grain state: `IProjectGrain` keyed by projectId, persisted to SQLite via Orleans grain storage.
- `CopilotPtySession` records the process exit first, drains pending output, then emits one terminal `Exited` event so the relay layer never sees exit before the last transcript chunk.

## Anti-Patterns

- Do NOT use Orleans Streams when a cloud relay (Web PubSub) already provides the transport. Double-hop adds latency and complexity.
- Do NOT enable Orleans clustering for a single-machine local broker. `UseLocalhostClustering()` with one silo only.
- Do NOT expose the broker to the network. All remote communication flows through the cloud relay.
- Do NOT log PTY content (input/output) — only metadata (message counts, session lifecycle events). Content may contain secrets.
- Do NOT publish `Exited` as soon as `WaitForExit` completes if the PTY reader can still produce buffered output.
