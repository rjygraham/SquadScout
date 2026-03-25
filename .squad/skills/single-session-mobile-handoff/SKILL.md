---
name: "single-session-mobile-handoff"
description: "Patterns for keeping a single-user MAUI project picker and active-session transcript flow coherent"
domain: "mobile"
confidence: "medium"
source: "earned"
---

## Context

Use this skill when a mobile client intentionally keeps one session in focus at a time and needs a clear picker → transcript handoff without redefining backend contracts.

## Patterns

- **Resume-first when occupied:** If a session is already active, convert the picker page into resume guidance instead of letting a generic start action silently reuse or replace the session.
- **Differentiate browse vs clear:** Returning to the project list should be non-destructive, while clearing the local session context should be an explicit separate action.
- **Model UX states in the view model:** Loading, empty, retry, and stale-selection states can stay entirely in computed view-model properties that feed XAML cards and empty views.
- **Hide composer affordances without a session:** Keep transcript empty-state actions focused on choosing a project, and only show the composer when a session actually exists.
- **Guard navigation against duplicate transcript pushes:** The active-session route should no-op if it is already on top, so resume/start actions do not stack duplicate pages.

## Anti-Patterns

- Do NOT let "Start session" silently navigate to or overwrite an existing in-focus session.
- Do NOT force project switching through backend changes when the ambiguity is really UI state and copy.
- Do NOT leave the transcript composer visible when there is no active session to send to.
