# Trinity History

## Day 1 Context

- User: Ryan Graham
- Project: Remote Copilot control from a .NET MAUI mobile app.
- Stack: .NET MAUI on .NET 10 communicating through Azure Web PubSub to a local .NET broker.
- Key concerns: intuitive mobile controls, session initiation, reconnect behavior, and project selection across multiple local repos.

## Learnings

- **Workstream Decomposition Pattern:** Mobile client work decomposes cleanly into 6 phases: infrastructure setup → datapath → session UX → hands-free RX → hands-free TX → resilience. This ordering respects broker dependencies and allows parallel execution of WS-1/3 while WS-2 unblocks audio features.

- **First Slice Strategy:** The MVP slice (WS-1/2/3 integration test) does not require Orleans, replay, or Entra—only a bare PubSub connection and hardcoded test messages. This validates the datapath in days, not weeks, before audio/reconnect complexity.

- **Constraint Recognition:** Single-user, single-session-at-a-time model means MAUI navigation is project selection → session (not sidebar multi-tab). Session persistence must survive app restart but not broker crash (Phase 1 assumption).

- **Dependency Management:** Most mobile blocks depend on Switch (message envelope) and Morpheus (session lifecycle). Early alignment with these teams on contracts (sequence numbering, replay API shape) is critical to unblock WS-2 on schedule.

- **Audio as Differentiator:** TTS + STT are full workstreams (WS-4/5), not afterthoughts. They require platform-specific testing (iOS/Android microphone/speaker perms) and deserve dedicated effort and design review for UX (record button prominence, confidence feedback).

### User Directives Accepted (2026-03-24)

- **Native chat UX confirmed.** No terminal-style rendering; SkiaSharp/xterm.js deferred indefinitely.
- **TTS/STT mandated for Phase 3.** On-the-go use case (including driving) requires accessibility; not optional.
- **Status:** WS-4 (TTS incoming) and WS-5 (STT outgoing) promoted to Phase 3 deliverables. Trinity ready to scaffold MAUI project and begin WS-4.

### 2026-03-24T18:15:00Z — Issue #9 Shell Scaffold Landed

- **Shell shape:** The MAUI app now uses a single-user, navigation-based architecture.
  - Entry point: project selection page (browse local repos, assign as "active project").
  - Terminal flow: session creation from active project → session terminal page.
  - Service composition: DI container registers auth (token cache), messaging (PubSub client), project state, session lifecycle.
- **Environment config:** Development fallbacks pre-configured (mock auth, in-memory project store, no Entra/PubSub required for debug builds).
- **Tests:** 6 unit tests cover shell composition, dev config, and session state updates.
- **PR #43 merge status:** Awaiting Switch review handoff.

### 2026-03-24T23:58:00Z — PR #43 Merged; Phase 1 Milestone Complete

- **Merge result:** PR #43 (Implement MAUI app shell scaffolding) successfully merged to main via GitHub.
- **Verification:** 36/36 tests pass, clean build, no conflicts, issue #9 auto-closed.
- **Phase 1 status:** Core shell is in place; ready for WS-2 (session datapath integration with messaging broker).
- **Next: WS-2 kickoff** depends on Switch team confirmation of message envelope contract. Morpheus will handle token validation; Link provides endpoints.

### 2026-03-24T17:31:17Z — GitHub Issues Backlog Imported

- **Import context:** Neo created GitHub issues #1–#34 in rjygraham/SquadScout with full phase gate preservation and routing labels.
- **Trinity ownership:** Issues #9 (MAUI shell), #10 (Transcript UI), #15 (UX polish), #21 (Reconnect), #27 (TTS), #28 (STT), #29 (Voice test).
- **Label pattern:** All issues tagged with `squad` + owner label (e.g., `squad:trinity`) + phase label (e.g., `phase:1`/`phase:3`).
- **Phase distribution:** Phase 1: #9, #10, #15. Phase 2: #21. Phase 3: #27, #28, #29.
- **Coordination note:** Trinity depends on Switch (#2 contract), Link (#13 endpoints), Morpheus (#12 token validation). Issue #16 gates Phase 1 completion.
- **Status:** All team histories updated. Ready for MAUI project scaffolding and Phase 1 kickoff.

### 2026-03-24T18:15:00Z — Issue #9 Shell Scaffold Landed

- **Shell shape:** The MAUI app now uses a single-user shell flow of project selection → active session, with one in-focus mobile session at a time.
- **Composition points:** Auth, project catalog, messaging, active-session state, and session lifecycle are registered behind app-side interfaces so #10/#21/#27/#28 can extend behavior without reshaping the host.
- **Local dev stance:** Environment-specific embedded app settings can fall back to seed projects and offline pending sessions in Development when the broker is unavailable, keeping session UX work reviewable before the full datapath is online.

### 2026-03-25 — Issue #10 Transcript UI Delivered

- **Session page shape:** The active-session page now renders as a native transcript surface with status banners, chat-style bubbles, a bottom composer, empty states, and stopped/error handling instead of a shell-state placeholder.
- **Projection seam:** A pure `SessionTranscriptController` now turns active-session snapshots plus messaging status into transcript view state. That keeps the UI testable today and gives #11 (live PubSub) / #21 (reconnect + replay) a stable extension point.
- **Interaction polish:** The page keeps a chat-like scroll-to-latest behavior when the user is near the bottom or sends a message, while preserving grouped spacing for consecutive user messages.
- **Validation:** `dotnet build .\SquadScout.slnx -nologo` and `dotnet test .\SquadScout.slnx -nologo --no-build` passed after landing the transcript UI and new app-side tests.

### 2026-03-25 — PR #49 Merge Watch Blocked on Switch Review

- **PR state:** GitHub shows PR #49 mergeable with `mergeable_state: clean`, but there are currently no reviews, no review comments, and no check runs on the head commit, so the approval gate has not been met.
- **Merge choice when unblocked:** Preferred path is **squash merge** once Switch approves because the branch is 17 commits ahead of `main` and includes a merge-from-main plus squad coordination commits that would create avoidable history noise if preserved.
- **Issue closure check:** The PR body still contains `closes #10`, so keeping the body intact at merge time will auto-close the transcript UI issue correctly.

### 2026-03-25 — PR #49 Reconciled and Merged

- **Approval handling:** Switch approval arrived as a supplied verdict artifact rather than a GitHub review object, so merge-readiness had to be confirmed from the handoff plus the absence of blocking GitHub review state.
- **Safe reconciliation pattern:** When adjacent mobile work lands first (`#11` before `#10`), the cleanest merge path is to fold the new transport affordance into the transcript shell instead of reviving the older placeholder cards. For this pass that meant keeping the native transcript UI, adding reconnect to the header actions, and refreshing transcript projection when message-connection status changes.
- **Validation:** After merging `origin/main` into `squad/10-maui-session-transcript-ui`, the combined branch passed `dotnet build .\SquadScout.slnx -nologo` and `dotnet test .\SquadScout.slnx -nologo --no-build` with 76/76 tests green.
- **Landing result:** PR #49 was squash-merged to `main` as `f61568e`, and issue #10 auto-closed via the preserved `closes #10` directive.

