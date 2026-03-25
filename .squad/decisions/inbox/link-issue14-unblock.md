# Link: Issue 14 unblock reassessment after #11 merge

## Context
- PR #48 (`closes #11`) merged to `main`, and `squad/14-pubsub-session-routing-group-membership` was rebased onto that updated mainline.
- The original #14 blocker is gone in one sense: the MAUI app now has a real `MessagingConnectionService` plus `PubSubNegotiationClient`, so client-side negotiate/connect/join logic is present on this branch.
- Issue #14 still needs session-scoped traffic to flow both ways through the approved group boundary before it is safe to open a PR.

## Decision
- Do **not** open PR #14 yet.
- Treat issue #14 as still blocked on the missing inbound Web PubSub command-ingress path from MAUI back to the broker.
- Keep the broker-side routing slice as-is; the remaining work is to add the cloud-side handler or other approved ingress seam that forwards client group messages into the broker session input path.

## Why
- `src\SquadScout.App\Services\MessagingConnectionService.cs` now sends client envelopes with `WebPubSubSendToGroupCommand`, so the client is no longer using the old stub path.
- `src\SquadScout.Broker\Program.cs` still only accepts live input through `POST /api/sessions/{sessionId}/input`, and `src\SquadScout.Functions` still exposes only `NegotiateFunction` with no Web PubSub event/upstream handler.
- That means broker → client PubSub fan-out exists, but client → broker ingress over the same session group does not. Opening a PR as “done” would hide a real end-to-end routing gap.

## Follow-up
- Coordinate with Seraph/Morpheus on the cloud/auth-facing ingress seam before claiming #14 complete.
- Once inbound Web PubSub events are forwarded into the broker session input route, rerun full validation and open the PR with `closes #14`.
