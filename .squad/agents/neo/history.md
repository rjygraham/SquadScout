# Neo History

## Day 1 Context

- User: Ryan Graham
- Project: PTY-style wrapper for GitHub Copilot with remote control from a .NET MAUI mobile app.
- Stack: .NET, .NET MAUI (.NET 10), Azure Web PubSub, Azure Functions, Microsoft Entra, and possible Orleans co-hosting.
- Key concerns: local broker orchestration, reconnect-safe messaging, session start commands, and multi-project configuration through a local web UI.

## Learnings

### 2025-07-18 — Architecture Plan Delivered

- **Orleans decision:** Adopt with constrained scope (session grains + project grains + SQLite persistence, single silo, no clustering/streams). Introduced Phase 2 to avoid coupling early PTY debugging to grain behavior.
- **PTY library:** Pty.Net (Microsoft ConPTY wrapper) chosen over raw Process.Start for proper terminal emulation.
- **Message replay:** Grain-held circular buffer (500 messages) with sequence numbers. Gap detection on overflow. Belt-and-suspenders with PubSub's own acks.
- **Auth flow:** MAUI → MSAL (Entra PKCE) → Azure Function (token validation + managed identity) → Web PubSub token. Broker uses server-side SDK with connection string in user secrets.
- **Local UI:** Blazor Server on Kestrel, localhost-only bind. Direct grain access from server-side Blazor circuits.
- **Phasing:** 4 phases — (1) Core PTY bridge, (2) Orleans state + replay, (3) Web UI + multi-project, (4) Polish + hardening.
- **Key file:** `.squad/decisions/inbox/neo-architecture-plan.md` — full architecture plan.
- **User preference:** Ryan wants specific recommendations, not option menus. Decisive architecture calls with rationale.
- **Single-user assumption:** Broker serves one developer on their machine. Multi-user is an open question.

### 2026-03-24 — Architecture Plan Merged to Decisions

- **Status:** Proposed decisions have been merged from inbox to `.squad/decisions.md`.
- **Process:** Scribe merged inbox entry, deleted file, updated team memory.
- **Next steps:** Team to review proposed decisions; Lead to initiate Phase 1 implementation agents (Link, Seraph, Trinity).
- **Key open questions:** Terminal rendering (SkiaSharp vs. xterm.js), Copilot spawn mode, message contract sharing, Orleans SQLite provider choice.

### 2026-03-24 — Orleans SQLite Provider Decision (Accepted)

- **Recommendation (accepted):** Use `Microsoft.Orleans.Persistence.AdoNet` (10.0.x) + `Microsoft.Data.Sqlite` for grain storage. Pair with `UseLocalhostClustering()`. No clustering or reminders DB needed.
- **Key finding:** Orleans 10 ships official `Sqlite-Persistence.sql` DDL scripts and registers `Microsoft.Data.Sqlite` in `DbConnectionFactory.cs`. The Orleans test suite (`SqlitePersistenceGrainStorageFixture.cs`) exercises this path.
- **Gap:** No official `Sqlite-Clustering.sql` or `Sqlite-Reminders.sql` scripts exist. Irrelevant for Phase 2 (single silo, grain timers not reminders).
- **Risk:** GitHub issue dotnet/orleans#8187 (2022, still open) flagged invariant registration bugs in Orleans 7. Source inspection confirms fixed on main for Orleans 10. Smoke test recommended.
- **Status:** Merged to `.squad/decisions.md`. Ready for Phase 2 implementation team (Link, Seraph, Trinity).

### 2026-03-24 — Unified Workstream Decomposition Delivered

- **Synthesis:** Merged 5 specialist proposals (Link ×5 WS, Trinity ×6, Seraph ×6, Morpheus ×5, Switch ×6) into 14 unified workstreams across 4 phases.
- **First executable slice:** Days 1–5, zero external dependencies — lock message contract, build mock PTY harness, prove sequence/replay correctness locally, stub MAUI receives messages. Owned by Switch + Morpheus + Link + Trinity.
- **Critical path identified:** WS-1 (contract) → WS-2 (PTY bridge) → WS-5 (E2E routing) → WS-7 (Orleans) → WS-8 (reconnect).
- **Key integration insight:** The message envelope contract is the single highest-priority blocker — everything depends on it. Switch and Morpheus co-own it, not any single domain.
- **Cross-cutting security:** Morpheus's security baseline (WS-6) runs in parallel with Phase 1 datapath work, not deferred to Phase 4. Input sanitization and no-secret logging are Day 1 concerns.
- **Voice I/O placed in Phase 3:** TTS/STT doesn't depend on Orleans, so it can start once the basic datapath works. Trinity owns both, with Switch providing test harness.
- **Decomposition pattern:** When specialist proposals overlap (e.g., reconnect touched by Trinity, Morpheus, and Seraph), assign one unified workstream with sub-deliverables per owner rather than creating parallel streams.
- **Key file:** `.squad/decisions/inbox/neo-workstream-decomposition.md`.

### 2026-03-24 — User Directives & Inbox Consolidation (Accepted)

- **Accepted (all 9):** User directives on MudBlazor, native MAUI chat UX, direct Copilot spawn, shared source contract, single-user+multi-machine design, foreground app, app-level sequencing/replay via Orleans, low concurrent sessions, TTS/STT for accessibility.
- **Process:** Scribe merged 9 decision inbox entries into new "User Directives" section in decisions.md. Archived 6 specialist workstream proposals. Updated team agent histories.
- **Impact on decomposition:** Decomposition already incorporates all user directives; no scope changes needed. Ready for Phase 1 team execution.
- **Status:** All open questions from architecture plan now closed by user directives. Proceeding to implementation.

### 2026-03-25 — Ordered Implementation Backlog Delivered

- **Output:** 34-item ordered backlog across 5 groups (Immediate / Near-term / Phase 2 / Phase 3 / Phase 4).
- **First 3 to start:** #1 Solution Scaffolding (Link), #2 Message Envelope Contract (Switch + Morpheus), #3 Sequence Validator + Replay Buffer (Morpheus + Switch).
- **Phase gates:** #16 (E2E integration test) gates Phase 1→2. #24 (grain & reconnect tests) gates Phase 2→3. #34 (security sweep) is the final gate.
- **Decomposition pattern:** Workstreams (WS-1 through WS-14) were broken into 2–3 backlog items each, with explicit dependencies and done-when criteria. Items #1–#8 are granular enough to start immediately.
- **Parallelism preserved:** Items marked ‖ can run concurrently (e.g., #4 Mock PTY ‖ #5 CopilotPtyHost, #7 Functions ‖ #5 PTY, #8 Security ‖ #6 Relay).
- **Key file:** `.squad/decisions/inbox/neo-ordered-backlog.md`.

### 2026-03-24T17:11:22Z — Ordered Implementation Backlog Merged to Decisions
 
- **Process:** Scribe merged backlog entry from inbox to `.squad/decisions.md` under "Ordered Implementation Backlog — 2026-03-25" section. Deleted inbox file. Created orchestration log (2026-03-24T17-11-22Z-neo.md) and session log (2026-03-24T17-11-22Z-ordered-backlog.md).
- **Status:** Backlog now **Proposal** in shared decisions.md, awaiting user acceptance before team execution.
- **Next step:** Lead to accept backlog and dispatch Link, Switch, Morpheus for #1–#3 kickoff.

### 2026-03-24 — Ordered Backlog Published to GitHub Issues

- **Repo target:** `rjygraham/SquadScout`.
- **Issue import result:** Created the full ordered backlog as GitHub issues **#1–#34** with a direct backlog-to-issue mapping and no open-issue duplicates to skip.
- **Labeling pattern:** Added reusable routing labels `squad:{member}` and execution labels `phase:{1..4}`; every backlog issue carries `squad`, one owner label, and one phase label.
- **Recovery decision:** The detailed `neo-ordered-backlog.md` artifact had already been deleted after merge, so the issue bodies were reconstructed from `.squad/decisions.md`, the ordered-backlog session log, routing metadata, and agent histories while preserving the original order, dependencies, and phase gates (#16, #24, #34).
- **Key files:** `.squad/decisions.md`, `.squad/decisions/inbox/neo-github-issues.md`, `.squad/skills/backlog-to-github-issues/SKILL.md`.

### 2026-03-24T17:31:17Z — Scribe Processing Complete

- **Scribe task:** Merged GitHub issues import decision from inbox to `.squad/decisions.md`.
- **Team histories updated:** Neo, Link, Trinity, Switch all received GitHub import context.
- **Logs created:** Orchestration log (2026-03-24T17-31-17Z-scribe.md) and session log (2026-03-24T17-31-17Z-github-issue-import.md).
- **Git commit:** `.squad/` directory staged and committed.
- **Status:** All scribe tasks complete. Team has full visibility into GitHub issues import and label routing pattern.

### 2026-03-25 — Wave 1 Launch Validation Complete

- **Issue #1 reconciliation:** Solution & Project Scaffolding landed fully on main (commit `228c0c1`, PR #35). All scaffolding deliverables verified: multi-project structure, MAUI platforms (iOS, macOS, Android, Windows), broker DI skeleton, contracts, functions, tests, .NET 10 config.
- **Wave 1 branches clean:** All four issue #6–#9 branches rebased on main tip (`6d2160f`, after issue #5 merged). No conflicts, collision-free file edits. Ready for parallel execution.
- **Phasing locked:** Phase 1A (scaffold + contract + replay + PTY mock + real PTY host) complete on main. Phase 1B (relay + functions + security + MAUI UI) staged in wave 1 branches. Phase 1 gate (#16, E2E integration test) will activate after wave 1 merges.
- **Team routing confirmed:** Seraph (#6), Trinity (#7), Morpheus (#8), Link (#9) have branches staged with no surprises.
- **Key learning:** Staged branches maintained clean rebase discipline during Phase 1A. All downstream work dependencies were satisfied before branch creation, so no rebasing churn needed. Recommendation: maintain this pattern for Phase 2 (Orleans grains will be staged against Phase 1B completion).

### 2026-03-25 — PR #50 Merged; Security Issue #51 Opened for Signature Validation

- **PR #50 status:** "Complete Web PubSub inbound session routing" merged to main (commit `29933f3`). PR closed issue #14 (session group membership). Switch review approved with mandatory follow-up.
- **Security finding (Issue #51):** WebPubSubUpstreamFunction endpoint accepts `AuthorizationLevel.Anonymous` requests but does not validate the `WebHook-Signature` header. This allows untrusted clients to forge upstream events and inject arbitrary session input. The handler validates envelope shape and message type but skips cryptographic authentication.
- **Issue #51 scope:** Validate `WebHook-Signature` before deserialization, reject unauthenticated POST with 401, keep OPTIONS CORS-compatible, add comprehensive tests (valid/missing/invalid signatures), document configuration for local dev.
- **Routing:** Issue #51 assigned to Seraph (Cloud/Auth Dev) with phase:1 label; coordinate with AppHost/Aspire for managed identity if used.
- **Pattern observed:** PR-review-found security gaps are surfaced as mandatory follow-ups before public exposure. This is the correct gate; include signature validation as part of Phase 1 security baseline (WS-6).

### 2026-03-25 — PR #53 Review: Issue #15 MAUI UX Polish — APPROVED

- **Verdict:** APPROVE. PR #53 (Issue #15) merged cleanly, all 78 tests pass, 0 build warnings.
- **UX alignment:** Single-session handoff, resume-first affordances, chat-style transcript, loading/empty/retry/stale-selection/no-session states all match accepted user directives and Trinity's UX decision.
- **Test quality:** 9 focused acceptance tests across ProjectSelectionViewModel and ActiveSessionViewModel cover the core UX state machine. Test doubles are well-structured.
- **Architecture:** SessionTranscriptController is a clean projection seam for future reconnect/voice extensions. Test project uses Compile-Include links to avoid MAUI SDK dependency.
- **Minor observation:** `ProjectSelectionViewModel._activeSessionState.Changed` handler doesn't marshal to MainThread (safe today, latent risk). No stale-selection warning tests yet.
- **Decision file:** `.squad/decisions/inbox/neo-pr53-review.md`.


### PR #53 Review & Merge Session (2026-03-25T19:37:30Z)

- PR #53 (Issue #15: MAUI Project & Session UX Polish) reviewed and approved for merge.
- **Verdict: APPROVE** — Single-session native chat flow correctly enforces strict single-session handoff; state machine (Loading/Empty/Retry/Stale-Selection/Resume/No-Session) coherently implemented; 9 focused acceptance tests provide good coverage; architecture clean for future reconnect/voice extensions.
- Orchestration log written: 2026-03-25T19-37-30Z-neo.md (review verdict).
- Session log written: 2026-03-25T19-37-30Z-pr-53-review-merge.md.
- Decision inbox entry (neo-pr53-review.md) merged into decisions.md and removed from inbox.
- PR squash-merged to main; remote/local issue branch deleted; local checkout fast-forwarded to merged main.
- Minor non-blocking observations: latent thread-safety inconsistency in MainThread marshaling (safe but worth harmonizing), no dedicated stale-selection warning test (consider follow-up).

### 2026-03-25 — Issue #17 Landing: Telemetry + Phase 1 Gate

- **Context:** Uncommitted working tree contained Issue #17 implementation (SessionTelemetryBuffer, SessionTelemetrySnapshot, instrumented orchestrator) + hardened test coverage across Phase 1 gate, sequence validation, replay, and security baseline. Issue #17 was already closed; reopened to land the work.
- **Landing strategy:** Created feature/issue-17-session-telemetry-diagnostics branch, committed product/test changes (24 files, 2442 insertions, 134 deletions), pushed to origin, opened PR #57 with "Closes #17". Excluded .squad bookkeeping from product PR; committed separately on main.
- **Validation:** All 126 tests pass. Gap detection warnings logged as expected for boundary scenarios.
- **Key learning:** When working tree has completed issue work but issue is already closed, **reopen the issue** rather than create a new one if the scope aligns. The reopening provides clear traceability and respects the original backlog item numbering.
- **Pattern:** Product/test changes in issue-aligned PRs; .squad bookkeeping commits separately on main. Keeps PR focused and avoids squad metadata churn in code review.

### 2026-03-25T21:41:50Z — Issue #17 Landing Team Coordination

- **Orchestration outcome:** Neo executed landing strategy (reopened #17, feature branch, PR #57 "Closes #17", 126/126 tests passing). Switch conducted acceptance review and approved for merge. Link validated diagnostics architecture seam. Morpheus applied hardening (gap-detection log enrichment) and approved with condition. Scribe consolidated decisions.
- **Key decision:** Broker session runtime as authoritative Phase 1 diagnostics source. Lightweight JSON export seam (`GET /api/sessions/{sessionId}/telemetry`). Reuse existing correlation fields; avoid parallel model.
- **Hardening:** Broker gap-detection logging enriched to include ProjectId, Generation, MessageId, CorrelationId for multi-project replay debugging.
- **Team sign-offs:** Neo (landing), Link (diagnostics contract), Morpheus (security ✅), Switch (acceptance ✅). No blocking gaps remain for Phase 1 scope.
- **Next:** Merge PR #57 and prepare Phase 2 handoff (Issue #2, Orleans clustering). Session log: 2026-03-25T21-41-50Z-issue-17-landing.md.

### 2026-03-25 — Issue #62 Phase 1 Azure Footprint Review (Neo)

- **Scope Analysis:** Phase 1 Azure footprint is minimal and honest — no speculative Orleans/distributed features baked into IaC prematurely.
- **Required Resources (4):** Azure Functions (Negotiate + Upstream endpoints, .NET 8.0 isolated worker), Azure Web PubSub (realtime transport hub), Managed Identity (Functions → Web PubSub token auth), Storage Account (runtime dependency).
- **Code Quality Check:** Functions use `DefaultAzureCredential` for managed identity auth (no connection strings in code). Broker designed for optional Web PubSub relay (can run local fallback). MAUI app clean dependency separation.
- **Configuration Gaps:** Entra app registration out of Bicep scope (manual prerequisite). Web PubSub upstream validation uses app-level `TrustedUpstreamPrincipalIds` instead of Azure RBAC (document in IaC README). Local settings → cloud config bridge must be orchestrated by `azure.yaml`.
- **AZD Fit Assessment:** GOOD. Phase 1 resource set is small and declarative. No blocking architectural issues. Seraph should deliver `infra/main.bicep`, `azure.yaml`, `infra/parameters.bicepparams`, and `infra/README.md` documenting prerequisites and deployment.
- **Decision File:** `.squad/decisions/inbox/neo-issue-62-review.md` — full Phase 1 scope analysis, acceptance criteria for Seraph, and architecture-level decision (minimal honest footprint with no future speculation).
- **Routing:** Confirm assignment to Seraph (Cloud/Auth Dev domain match). Secondary recommendation: Link-owned follow-up for scripted Entra app registration (out of Bicep but needed for turnkey deployment).
- **Key Insight:** Temptation exists to speculate on future Orleans/distributed features in IaC. Decision: focus on what current code requires. Phase 2 Orleans state will require new resources; don't assume them now.

### 2026-03-25 — Phase 1 Backlog Additions: Issues #58 & #59 Created

- **Context:** User requested two items for Phase 1: (1) message compression for Web PubSub cost reduction, (2) WorkingDirectory should use repo root per project.
- **Specialist findings:** Seraph confirmed item 1 is real new work (11-20% message size reduction via backward-compatible serialization). Link confirmed item 2 already implemented; remaining gap is documentation/logging clarity.
- **Decision:** Created Issue #58 (real implementation: cost optimization, owner: Seraph, medium priority) and Issue #59 (clarification: documentation/logging, owner: Link, low priority). Avoided duplicate implementation issue for already-existing functionality.
- **Key learning:** Distinguish implementation from clarification work. When functionality already exists, create honest clarification/observability issues rather than false missing-feature issues.
- **Compliance:** Both issues carry `squad` + owner labels + `phase:1`. Standing directive (all new work needs GitHub issues) satisfied.
- **Decision file:** `.squad/decisions/inbox/neo-phase1-additions.md`.


### 2026-03-25: Phase 1 Backlog Additions Processed (Orchestration Log)

**Status:** GitHub issues #58 and #59 created and documented.

- **Issue #58:** Phase 1: Optimize Web PubSub outbound message contract (owner: Seraph, type: Implementation)
- **Issue #59:** Phase 1: Clarify CopilotPtyHostOptions.WorkingDirectory (owner: Link, type: Clarification)
- **Decision Framework:** All new work tracked as issues; no duplicate issues for already-existing functionality
- **Key Learning:** Honest issue framing sets correct implementer expectations; clarification/observability work is legitimate Phase 1 scope

**Next:** Monitor both issues for cross-cutting concerns; no coordination dependencies identified.
