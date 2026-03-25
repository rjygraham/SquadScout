# Team Decisions

## Issue #3 / PR #37 — Sequence Validator Review Outcome

**Timestamp:** 2026-03-24T19:30:09Z  
**Reviewer:** Switch  
**Verdict:** **REJECTED**

### Reviewed Artifact

- Issue: #3
- PR: #37
- Branch: `squad/3-sequence-validator-replay-buffer`
- Implementation commit: `8068e81`

### Verification Results

- `dotnet build .\SquadScout.slnx -nologo` ✅
- Focused broker review filter (`SessionSequenceValidatorTests|CircularReplayBufferTests|InMemorySessionOrchestratorReplayTests`) ✅ 12/12 passed
- `dotnet test .\SquadScout.slnx -nologo --no-build` ✅ 20/20 passed

### What Is Good

- Broker-side client validation enforces broker-owned replay sequence and monotonic client sequencing
- Cumulative acknowledgement handling is monotonic and idempotent for accepted/duplicate traffic, while gap detection freezes ack advancement
- Replay buffer overflow surfaces explicit window metadata and gap signaling
- Generation reset boundaries correctly return an empty replay response with current-generation metadata
- Heartbeats are excluded from replay storage
- Session-id trust-boundary rejection is covered

### Required Fixes Before Approval

1. **Add explicit future-generation failure coverage**
   - The validator returns `SequenceValidationStatus.FutureGeneration`, but there is no focused test that proves this path or verifies ack state remains unchanged
   - Add coverage in `tests\SquadScout.Broker.Tests\SessionSequenceValidatorTests.cs`

2. **Add explicit project-id mismatch replay rejection coverage**
   - Replay currently rejects wrong `ProjectId`, but the suite only exercises wrong `SessionId`
   - Add a separate trust-boundary test in `tests\SquadScout.Broker.Tests\InMemorySessionOrchestratorReplayTests.cs`

### Revision Assignment

- **Recommended next owner:** Link
- **Why:** Morpheus authored the rejected artifact and is locked out for the next revision cycle

### Reviewer Note

No material implementation bug was found during this pass. Rejection is solely because the required failure-mode coverage for signoff is incomplete, and Switch charter forbids approval without it.

---

## Issue #9 / PR #43 — MAUI App Shell Scaffolding Merge

**Timestamp:** 2026-03-24T23:45:00Z  
**Merger:** Trinity (squad automation)  
**Verdict:** **MERGED**

### Reviewed Artifact

- Issue: #9
- PR: #43
- Branch: `squad/9-maui-app-shell-scaffolding`

### Verification Results

- Build: ✅ Clean, no errors or warnings
- Tests: ✅ All 36 pass (SquadScout.App.Tests + SquadScout.Broker.Tests)
- Mergeable state: ✅ Clean
- Reviews: ✅ Switch approved (no blocking reviews)

### Scope

- Shell scaffolding and navigation flow (project selection + active sessions)
- Service composition for auth, messaging, session lifecycle
- Development configuration (environment-aware)
- Unit tests for mobile shell development fallbacks
- Files changed: 24 (+1758/-40 insertions)

### Merge Strategy

Merge commit (`--no-ff`) preserves feature branch history and documents feature boundary.

### Merge Outcome

- ✅ Merged successfully on 2026-03-24T23:58:09Z
- Main commit: `7553a47` (Merge pull request #43)
- Issue #9 auto-closed as "completed"
- Trinity history updated with milestone notes

### Next Phase

WS-2 kickoff: Await Switch team message envelope contract finalization. Link will provide endpoint configuration; Morpheus will validate tokens in session initiation flow.

---

## Issue #12 / PR #45 — Token Validation & Session Claims Hardening Merge

**Timestamp:** 2026-03-25T00:45:00Z  
**Decision Owner:** Morpheus (Merge Decision)  
**Verdict:** **MERGE (Squash)**

### Reviewed Artifact

- Issue: #12
- PR: #45
- Branch: `squad/12-token-validation-session-claims-hardening`
- Implementation commit: `5e7f232`

### Merge Readiness

- ✅ No review blockers
- ✅ No merge conflicts
- ✅ Mergeable state: Clean
- ✅ Build: ✅ Green
- ✅ Tests: ✅ 14/14 (focused), 61/61 (full)
- ✅ Security validation: Confirmed

### Security Review Highlights

- Proper fail-closed Easy Auth handling
- Header tampering rejection enforced
- Broker scoping validation in place
- Session isolation confirmed
- Response hygiene validated

### Merge Strategy Rationale

**Strategy: Squash**
- Single logical unit (token validation middleware + session claims hardening)
- Encapsulates WS-2 token validation workstream
- Reduces main branch fragmentation
- Minimizes downstream conflict surface
- Preserves closure semantic: GitHub auto-closes issue #12

### Merge Outcome

✅ Ready for merge. Issue #12 closure gates Phase 1 security hardening.
