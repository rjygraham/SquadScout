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
