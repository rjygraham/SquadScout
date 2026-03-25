---
name: "webpubsub-upstream-auth-validation"
description: "Secure Azure Web PubSub upstream endpoints without breaking local development or Easy Auth deployments"
domain: "security"
confidence: "high"
source: "earned"
---

## Context
Use this when a SquadScout Azure Function or other upstream endpoint accepts Azure Web PubSub CloudEvents. The endpoint is often anonymous at the Functions trigger layer, so request trust must be re-established before event processing or broker forwarding.

## Patterns
- **Validate origin first:** Require `WebHook-Request-Origin` to match the configured Web PubSub service host before considering any other auth signal.
- **Prefer dual-path trust:** Accept either a trusted Easy Auth principal inside the Functions host boundary (verified by `WEBSITE_INSTANCE_ID`) or a valid signature generated from configured Web PubSub access key(s). This keeps cloud auth strict while preserving local broker/tunnel workflows.
- **Use the CloudEvents signature contract:** Validate `ce-signature` (and optionally `WebHook-Signature` as an alias) by computing `sha256=<HEX(HMACSHA256(accessKey, connectionId))>` from `ce-connectionId`. Do not invent a body-hash scheme.
- **Authenticate before parsing:** Reject unauthorized POSTs before checking event type or deserializing the body so forged envelopes never reach app logic.
- **Make config explicit:** Use separate config for key-based validation (`WebPubSubUpstreamAccessKeys`) and Easy-Auth caller allow-lists (`TrustedUpstreamPrincipalIds`).
- **Validate the session-group contract before forwarding:** Once the envelope is authenticated, prove `projectId` and `sessionId` can form the expected Phase 1 group (`session:{projectId}:{sessionId}`) before calling the broker so malformed routing inputs fail with cloud-side diagnostics instead of downstream relay exceptions.
- **Log with datapath correlation fields:** Include `projectId`, `sessionId`, derived `sessionGroup`, `messageId`, and `ce-connectionId` in broker-forwarding success/failure logs to make Phase 1 gate failures repeatable and diagnosable.

## Examples
- `src\SquadScout.Functions\Upstream\WebPubSubUpstreamAuthenticator.cs` authenticates Azure Web PubSub upstream POSTs with origin checking plus Easy Auth or signature validation.
- `src\SquadScout.Functions\Upstream\WebPubSubUpstreamHandler.cs` calls the authenticator before event routing or JSON parsing.
- `tests\SquadScout.Broker.Tests\PubSubUpstreamHandlerTests.cs` covers accepted signed requests, accepted managed-identity requests, rejected missing/invalid/untrusted requests, and rejected envelopes that cannot map to a legal Phase 1 session group.

## Anti-Patterns
- Do NOT trust `AuthorizationLevel.Anonymous` plus obscurity or route secrecy.
- Do NOT trust Easy Auth headers outside the Azure Functions/App Service host boundary.
- Do NOT skip validation just because the Function uses managed identity for outbound Web PubSub operations; outbound identity does not authenticate inbound callbacks.
- Do NOT validate the wrong payload shape; Azure Web PubSub upstream signatures are based on `connectionId`, not the raw request body.
