---
name: "message-envelope-replay-safety"
description: "Patterns for designing realtime message envelopes that stay safe under reconnect, replay, and duplicate delivery"
domain: "api-design"
confidence: "medium"
source: "earned"
---

## Context

Use this skill when a broker and client exchange realtime session traffic over an unreliable or reorder-prone transport and later need replay, reconnect, or gap recovery. It is especially relevant when the transport is authenticated but not trusted to preserve perfect ordering or exactly-once delivery.

## Patterns

- **Sequence only replayable traffic:** Put a monotonic server-assigned sequence on replayable broker → client frames. Keep heartbeat, ack, and input-control traffic out of the replay stream unless replaying them is a deliberate requirement.
- **Make sequence authority explicit:** If one envelope type is reused in both directions, document whether `sequence` is broker-owned, client-owned, direction-scoped, or nullable. If both sides need counters, split them into separate fields instead of implying one shared ordering domain.
- **Treat ack as a cumulative high-water mark:** Use `ackUpToSequence` to report the highest contiguous sequence the client has applied. Make it monotonic and idempotent so retries are harmless.
- **Keep ack in one place:** Put the cumulative acknowledgement on the top-level envelope, not duplicated inside heartbeat payloads, so replay and reconnect code have one source of truth.
- **Define the ordering identity explicitly:** If ordered state can reset without changing `sessionId`, add a generation/restart marker. Otherwise guarantee a fresh session id on any reset.
- **Replay must advertise the available window:** Include available head/tail sequence values and an explicit gap/overflow signal so the client can recover safely instead of silently skipping lost data.
- **Sequence beats timestamp:** Timestamps are for diagnostics, leases, and correlation. Ordering, dedupe, and replay should use sequence plus stable message identifiers.
- **Version the envelope early:** Add contract versioning before multiple components ship against the first draft.

## Examples

- Replay request: `{ projectId, sessionId, afterSequence, expectedGeneration, correlationId }`
- Replay response: `{ generation, availableFromSequence, availableToSequence, gapDetected, messages: [...] }`
- Heartbeat / ack control frame: `{ sessionId, ackUpToSequence, issuedAtUtc, expiresAtUtc, correlationId }`

## Anti-Patterns

- Do not let heartbeat chatter consume replay buffer capacity by default.
- Do not place the same `sequence` field on client → broker and broker → client frames without stating who owns it or whether the counters are independent.
- Do not rely on transport message ids or timestamps for transcript ordering.
- Do not allow overflow/gap behavior to be implied; surface it in the contract.
- Do not reuse a session identity after ordered state resets unless the contract also carries generation metadata.
