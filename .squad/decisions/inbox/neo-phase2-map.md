### 2026-03-26: Neo — Phase 2 Execution Map

**By:** Neo (Lead)
**What:** Dependency-correct execution order, wave assignments, and parallel-start decisions for all remaining Phase 2 issues.

---

#### Dependency Graph (resolved)

All Phase 1 prerequisites (#3, #6, #7, #11, #13, #14, #15, #16) are merged to main.

```
#18 ──┬──▸ #19 ──▸ #21 ──┬──▸ #22 ──┐
      │                   └──▸ #23 ──┤
      └──▸ #20 ─────────────────────┤
                                     ▼
#54 (independent) ──────────────▸  #24 (gate)
#55 (independent) ──────────────▸
```

Critical path: **#18 → #19 → #21 → {#22 ‖ #23} → #24**

#### Wave 0 — Start Now (no blockers)

| Issue | Owner | Branch / Worktree | Notes |
|-------|-------|-------------------|-------|
| #18 | Link | `squad/18-…` / `SquadScout-18` ✅ | Orleans silo + SQLite. Critical path root. |
| #54 | Switch | `squad/54-…` / `SquadScout-54` ✅ | Duplicate broker-message detection. Independent of Orleans. |
| #55 | Switch | needs `squad/55-…` / `SquadScout-55` | Lower-gen broker reset rejection. Same MAUI transport layer as #54 but orthogonal validation. Start in parallel — or immediately after #54 if Switch prefers sequential. |

#### Wave 1 — After #18 merges

| Issue | Owner | Notes |
|-------|-------|-------|
| #19 | Link | Session Grain & Durable Replay State. **Critical path** — unlocks #21. Start immediately after #18. |
| #20 | Link | Project Grain & State Migration Path. Can overlap with #19 (different grain) or follow it. Not on critical path. |

#### Wave 2 — After #19 merges

| Issue | Owner | Notes |
|-------|-------|-------|
| #21 | Trinity | Reconnect & Replay Resume Flow. Unlocks Wave 3. |

#### Wave 3 — After #21 merges (parallel)

| Issue | Owner | Notes |
|-------|-------|-------|
| #22 | Morpheus | Heartbeat & Liveness Model. |
| #23 | Seraph | Token Refresh & Session Rejoin Flow. |

#### Wave 4 — After ALL #18–#23 merge

| Issue | Owner | Notes |
|-------|-------|-------|
| #24 | Switch | Phase 2 → Phase 3 gate test suite. |

#### Decision: #55 starts now

**Rationale:** #55 has zero dependency on the Orleans chain. It touches mobile-client generation-monotonicity validation — orthogonal to #54's dedup concern. Both are small, focused changes. Starting now keeps Switch productive while Link bootstraps Orleans (#18). If Switch capacity is tight, #55 can follow #54 immediately since they share the same MAUI transport surface.

#### Branch & Merge Rules (user directive, reaffirmed)

- Each issue gets its own branch: `squad/{issue}-{slug}`.
- Each issue gets its own worktree: `D:\GitHub\SquadScout-{issue}`.
- Commit messages reference the issue: `Refs #{issue}` or `Closes #{issue}`.
- PR titles and bodies reference the issue.
- Merge only when branch is clean (build ✅, tests ✅, review approved).
- Squash merge to main; delete feature branch after merge.

#### Housekeeping Note

Stale worktrees from closed issue #69 remain (`SquadScout-69-livepath`, `SquadScout-69-self`). Ralph should clean these up.

#### Immediate Launch Sequence

1. **Link → #18** (worktree ready, start now)
2. **Switch → #54** (worktree ready, start now)
3. **Switch → #55** (create worktree, start now or immediately after #54)
4. After #18 lands: **Link → #19** (critical path)
5. After #19 lands: **Trinity → #21**
6. After #21 lands: **Morpheus → #22 ‖ Seraph → #23**
7. After all merge: **Switch → #24** (gate)
