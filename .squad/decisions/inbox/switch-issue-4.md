# Switch Decision Note — Issue #4 Mock PTY Harness

- **Requested by:** Ryan Graham
- **Branch:** `squad/4-mock-pty-harness`
- **Issue:** #4 Mock PTY Harness

## Decision

Adopt a two-layer PTY seam:

1. **Broker-facing host contract:** `src\SquadScout.Broker\Pty\IPtyHost.cs` starts PTY sessions from `PtySessionStartRequest`.
2. **Per-session PTY contract:** `src\SquadScout.Broker\Pty\IPtySession.cs` owns raw writes, event reads, termination, and lifecycle state.

The mock implementation (`MockPtyHost` / `MockPtySession`) stays **event oriented** and emits `PtySessionEvent` values (`Started`, `Output`, `Exited`) with deterministic logical-tick scheduling. It does **not** mint broker envelopes, sequence numbers, replay metadata, or transport concerns.

## Why

- Preserves Link's seam requirement that PTY simulation stay host-shaped and transport-free.
- Keeps sequencing/replay ownership in `src\SquadScout.Broker\Sessions\`.
- Gives issue #5 a drop-in contract for the real Copilot PTY host.
- Gives issue #6 a clean place to translate PTY events into broker envelopes and relay publication.

## Coupled follow-on choices

- `IRelayPublisher` now exposes `PublishEnvelopeAsync<TPayload>` so tests and later relay code can observe broker envelope publication without hiding sequencing inside ad hoc helpers.
- `SessionRuntimeState` updates `SessionDescriptor.State` when it records `SessionLifecyclePayload`, making running/stopped transitions visible through `ISessionOrchestrator.GetAsync`.
- Typed payloads now exist for `Input`, `Output`, and `SessionLifecycle` under `src\SquadScout.Contracts\Messages\`.

## Verification

- `dotnet build .\SquadScout.slnx -nologo`
- `dotnet test .\SquadScout.slnx -nologo --no-build`
