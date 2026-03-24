---
name: "backlog-to-github-issues"
description: "Convert an ordered local backlog into GitHub issues without losing routing, sequence, or dependency intent"
domain: "planning"
confidence: "high"
source: "earned"
---

## Context

Use this when a local backlog already exists in Squad memory and needs to become real GitHub issues for execution. This is especially useful after Scribe has merged or deleted the original inbox artifact and the lead needs to reconstruct issue bodies from canonical summaries, logs, and agent history.

## Patterns

- **Check open issues first:** Query existing open issues before creating anything. Skip obvious duplicates instead of creating parallel backlog entries.
- **Preserve order in titles:** Prefix each issue title with a zero-padded backlog number such as `Backlog #01:` so execution sequence is visible in GitHub lists.
- **Carry order into bodies too:** Include backlog position, phase, owner, collaborators, dependencies, and gate role in the issue body so ordering survives copies and exports.
- **Use one routing owner label:** Apply `squad` plus exactly one `squad:{owner}` label per issue. Put collaborators in the body rather than adding multiple owner labels.
- **Add phase labels for batching:** Add `phase:{n}` labels so Ralph and future coordinators can filter issues by execution stage.
- **Reference dependencies as soon as they exist:** Create issues in backlog order. Then later issues can say `Depends on: #12 (backlog #12)` instead of vague prose.
- **If the source artifact is gone, reconstruct from canonical memory:** Use `.squad/decisions.md`, session logs, routing, and agent histories to rebuild meaningful issue bodies while preserving the original gates and critical path.
- **Record reconstruction decisions:** If you had to infer wording or restore deleted detail, write that decision to `.squad/decisions/inbox/` so the team understands why the imported issues look the way they do.

## Examples

- Convert a 34-item ordered backlog into issues `#1`–`#34`, using titles like `Backlog #16: End-to-End Phase 1 Datapath Gate`.
- Add labels `squad`, `squad:switch`, and `phase:1` to the Phase 1 gate issue while keeping cross-team collaborators in the body.
- When `neo-ordered-backlog.md` is no longer present, rebuild issue bodies from `.squad/decisions.md` and specialist histories instead of blocking on the missing file.

## Anti-Patterns

- Do NOT create issues with unordered titles like `Build broker relay` if the backlog was intentionally sequenced.
- Do NOT apply multiple `squad:{owner}` labels to one issue just because there are collaborators.
- Do NOT recreate obvious open-issue duplicates.
- Do NOT drop gate semantics (#16, #24, #34 in this repo) when moving the backlog into GitHub.
- Do NOT silently reconstruct missing backlog detail; capture that decision in team memory.
