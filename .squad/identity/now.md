# Current Focus

The squad is executing Phase 2 to completion: Orleans state, durable replay, reconnect safety, and mobile replay-path hardening.

Immediate focus (Wave 0):

- Issue #18 (Link): Orleans Silo Host & SQLite Bootstrap — critical path root, worktree ready.
- Issue #54 (Switch): Detect duplicate broker messages on mobile replay path — worktree ready.
- Issue #55 (Switch): Reject lower-generation broker resets on mobile replay path — start now or immediately after #54.

Pipeline: #18 → #19 → #21 → {#22 ‖ #23} → #24 (gate). See `.squad/decisions/inbox/neo-phase2-map.md` for full execution map.

