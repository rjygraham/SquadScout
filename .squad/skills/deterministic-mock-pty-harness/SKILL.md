---
name: "deterministic-mock-pty-harness"
description: "Patterns for building a transport-free PTY simulator that proves broker datapaths in fast offline tests"
domain: "testing"
confidence: "medium"
source: "earned"
---

## Context

Use this skill when a broker needs to prove PTY-driven input/output, lifecycle, sequencing, and replay behavior before a real PTY dependency or cloud relay is available. It is especially useful for Phase 1 style slices where the goal is deterministic CI coverage, not process fidelity.

## Patterns

- **Split host from session:** Keep a broker-level `IPtyHost` that starts sessions and a per-session `IPtySession` that owns writes, reads, termination, and lifecycle state.
- **Emit PTY events, not broker envelopes:** The PTY seam should surface `Started`, `Output`, and `Exited` events. Let the broker/session layer translate those into sequenced envelopes.
- **Use logical ticks instead of sleeps:** Mock PTY timing should advance via explicit `AdvanceBy(...)` or `ReleaseNext()` controls so chunking and exit ordering stay reproducible in CI.
- **Keep mock controls mock-specific:** Deterministic scheduling helpers, queued failures, and captured writes should live on the mock implementation, not on the shared PTY interface.
- **Capture relay publication separately:** Pair the PTY mock with a recording relay publisher and a small test fixture that pumps events through the broker orchestration layer. This lets one test assert chunk boundaries, relay order, replay windows, and visible session state.
- **Model lifecycle with typed payloads:** Use explicit `SessionLifecyclePayload` and `OutputChunkPayload` types so later relay work does not depend on anonymous payloads or `JsonElement` guesses.
- **Add an HTTP gate harness when contracts matter:** For true Phase 1 gate coverage, host the broker in-process (for example with `WebApplicationFactory<Program>`) and swap in a seeded project catalog, `MockPtyHost`, and `RecordingRelayPublisher`. This catches serializer/config drift at the HTTP boundary while still keeping PTY behavior deterministic.

## Examples

- `src\SquadScout.Broker\Pty\IPtyHost.cs`
- `src\SquadScout.Broker\Pty\IPtySession.cs`
- `src\SquadScout.Broker\Pty\MockPtySession.cs`
- `tests\SquadScout.Broker.Tests\TestDoubles\MockPtyHarnessFixture.cs`
- `tests\SquadScout.Broker.Tests\MockPtyHarnessIntegrationTests.cs`
- `tests\SquadScout.Broker.Tests\BrokerPhase1DatapathGateTests.cs`

## Anti-Patterns

- Do not let the PTY mock assign broker sequence numbers or replay metadata.
- Do not use wall-clock sleeps, timers, or background polling to release output.
- Do not couple the PTY contract to Web PubSub, HTTP, or message-envelope types.
- Do not hide lifecycle transitions inside opaque test helpers; surface them as explicit PTY events and broker lifecycle payloads.
