# Link: Issue 14 session routing slice

## Context
- Issue #14 needs broker and MAUI traffic isolated to the approved session group contract.
- Issue #11 is still missing the MAUI Web PubSub connection service, so a full live join/leave and command-ingress path is not yet available in this branch.
- Shared contracts already lock the group naming pattern as `session:{projectId}:{sessionId}[:brokerId]`.

## Decision
- Centralize broker-side session group resolution in a dedicated `SessionGroupResolver` and use the base `session:{projectId}:{sessionId}` group for the current single-broker path.
- Let the broker explicitly track its own join/leave lifecycle at session start/stop and publish PTY envelopes to that resolved group through the Azure Web PubSub service SDK when a broker connection string is configured.
- Resolve the same group for accepted inbound input now, but do not fake a second transport path before issue #11 lands; actual MAUI Web PubSub command ingress remains blocked on the missing client connection service.

## Why
- This keeps the session routing rule single-sourced and observable on the broker side instead of scattering string building across relay code and tests.
- It preserves the later broker-affinity suffix seam without prematurely activating broker-specific groups in the current single-broker flow.
- It avoids inventing a partial client transport that would likely conflict with Trinity’s pending #11 work while still moving the broker/cloud slice forward cleanly.

## Follow-up
- Issue #11 should wire the MAUI connection service to the same base session group and complete live join/leave plus inbound command delivery over Web PubSub.
- When broker affinity becomes real work, extend `SessionGroupResolver` to append the optional broker suffix rather than changing multiple call sites.
