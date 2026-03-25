# Scribe History

## Day 1 Context

- User: Ryan Graham
- Team: Neo, Trinity, Link, Seraph, Morpheus, Switch, Scribe, and Ralph.
- Project: A remote Copilot PTY wrapper with a local .NET broker, Azure-backed realtime transport, and a .NET MAUI client.
- Key duties: preserve the decision log, record orchestration activity, and keep cross-agent context synchronized.

## Learnings

- **Orchestration pattern:** Handoff → Review Gate → Merge path is the primary team workflow. Log both sides (implementer + reviewer) at same timestamp.
- **Decision inbox:** Pending decisions in `.squad/decisions/inbox/` are merged into `decisions.md` when associated work completes. Merged files are removed to keep inbox tidy.
- **Cross-agent sync:** When an agent rejects or reassigns work, update both the decisions log (for future reference) and the orchestration log (for live agent coordination).
- **Session logs:** Brief records of issue/PR handoffs capture milestone completions and link session history to live work.
- **Timestamp discipline:** Use ISO 8601 hyphenated format (e.g., `2026-03-25T00:17:15Z`) for consistency with squad file naming conventions.