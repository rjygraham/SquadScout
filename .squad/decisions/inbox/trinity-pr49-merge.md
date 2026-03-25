# Trinity — PR #49 Merge Watch

## Context

PR #49 (`Implement MAUI session transcript UI`) is the merge vehicle for issue #10 and currently keeps `closes #10` in the PR body. GitHub reports the PR as mergeable/clean, but there is no Switch approval yet and no check runs are attached to the head commit.

## Decision

- Do not merge PR #49 until Switch leaves an explicit approving review.
- If Switch approves and the diff still matches the intended issue #10 transcript UI delivery, use **squash merge**.
- Keep the existing PR body text so GitHub still auto-closes #10 on merge.

## Why

The branch is cleanly mergeable, but review state is the active gate and the task explicitly requires a Switch approval before merging. Squash is the lowest-noise path because the branch currently carries 17 commits, including a merge-from-main commit and squad coordination commits that would add avoidable history churn if preserved individually.

## Follow-up

- Re-check PR reviews immediately before merge to confirm Switch approved.
- Re-check mergeability immediately before merge in case `main` advanced.
- If approval arrives and the PR remains clean, merge with squash so `main` gets one tidy issue #10 commit and the PR body still closes #10.
