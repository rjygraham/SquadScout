---
name: "bidirectional-envelope-ordering"
description: "Patterns for keeping broker replay ordering separate from client control sequencing in a shared message envelope"
domain: "api-design"
confidence: "medium"
source: "earned"
---

## Context

Use this skill when the same envelope type travels in both directions between a broker and a client, but only one direction participates in replay, reconnect, or transcript ordering. It helps prevent client-authored control frames from being mistaken for broker-owned replay state.

## Patterns

- **Separate ownership domains:** Reserve broker `sequence` for replayable broker→client traffic and add a distinct `clientSequence` for client→broker dedupe or correlation when needed.
- **Make ordered identity explicit:** Define replayable broker frames by `{ sessionId, generation, sequence }` so reconnect logic can distinguish resumed state from a reset stream.
- **Echo, do not mint:** Client frames should echo the latest broker `generation`; only the broker can mint a new generation.
- **Reset acknowledgements with generation:** Treat cumulative acknowledgements as generation-scoped high-water marks and clear them when generation changes.
- **Omit null optionals on the wire:** Prefer omitting unused sequence fields rather than serializing placeholder nulls that suggest a shared ordering domain.

## Examples

- Broker output frame: `{ sessionId: "sess-1", generation: 4, sequence: 98, messageType: "output" }`
- Client replay request: `{ sessionId: "sess-1", generation: 4, clientSequence: 22, acknowledgedSequence: 97, payload: { fromSequenceInclusive: 98 } }`
- First broker frame after PTY restart with same session id: `{ sessionId: "sess-1", generation: 5, sequence: 1 }`

## Anti-Patterns

- Do not reuse one shared `sequence` field in both directions without stating who owns it.
- Do not carry acknowledgements from generation 4 into generation 5.
- Do not reset ordered broker state under the same session id unless the broker also increments generation.
