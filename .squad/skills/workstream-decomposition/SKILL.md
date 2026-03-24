---
name: "workstream-decomposition"
description: "Patterns for synthesizing multiple specialist proposals into a unified build plan"
domain: "architecture"
confidence: "medium"
source: "earned"
---

## Context

When multiple domain specialists each produce independent workstream proposals, the lead must synthesize them into one coherent build plan that eliminates overlaps, surfaces the critical path, and gives each workstream a single clear owner.

## Patterns

- **Contract-first sequencing:** Identify the shared contract (message envelope, API schema, data model) and make it the very first deliverable. Everything else gates on it.
- **First executable slice = zero external dependencies:** The first thing you build should prove the core assumption (e.g., "messages flow end-to-end") using only local mocks, in-memory transport, and test harnesses. No cloud, no database, no third-party service.
- **Merge overlapping workstreams into sub-deliverables:** When 3 specialists each propose reconnect work, create one "Reconnect" workstream with sub-deliverables per owner, not 3 parallel streams. Prevents integration surprises.
- **Security baseline in Phase 1, hardening in Phase 4:** Input sanitization, no-secret logging, and rate limiting are Day 1 concerns. TLS pinning, audit, and penetration testing are Phase 4.
- **Phase boundaries at risk boundaries:** Each phase should prove one risky assumption. Phase 1 = datapath works. Phase 2 = state survives restarts. Phase 3 = rich UX (voice, multi-project). Phase 4 = production readiness. Don't mix risks.
- **Owner matrix over Gantt charts:** A table of (Agent × Phase → Workstream) is more actionable than a timeline. Teams can self-schedule within phases.
- **Carry open questions explicitly:** Don't silently resolve ambiguity. List open questions at the end so the user knows what's still undecided.
- **Feature flag for migrations:** When migrating from in-memory to durable state (e.g., Phase 1 → Phase 2 Orleans), propose a feature flag for safe cutover rather than a hard switchover.

## Anti-Patterns

- Do NOT let each specialist's proposal become its own execution track. You get 5 parallel plans with integration gaps at every boundary.
- Do NOT defer security to the last phase. Logging redaction and input sanitization are cheap to add early and expensive to retrofit.
- Do NOT create a first slice that requires cloud provisioning. If Day 1 depends on Azure/AWS, you've already lost a week to provisioning latency.
- Do NOT assign a workstream to "the team" — every deliverable needs exactly one owner (with explicit collaborators).
