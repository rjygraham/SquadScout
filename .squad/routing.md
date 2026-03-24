# Routing

## Default Routes

- Architecture, decomposition, cross-cutting tradeoffs, and reviewer gates -> Neo
- .NET MAUI app work, mobile UX, session command initiation, and client-side state -> Trinity
- Local broker host, PTY wrapper behavior, project registration UI, and Copilot process orchestration -> Link
- Azure Web PubSub, Azure Function integration, Entra auth, and cloud wiring -> Seraph
- Security reviews, performance analysis, replay safety, resiliency, and threat modeling -> Morpheus
- Testing strategy, reconnect/replay coverage, regression checks, and reviewer feedback -> Switch
- Decision merges, session logging, orchestration logging, and shared memory maintenance -> Scribe
- Backlog monitoring, GitHub issue pickup, PR follow-through, and idle detection -> Ralph

## Multi-Agent Patterns

- End-to-end session architecture -> Neo + Link + Trinity + Seraph + Morpheus
- Reconnect and replay design -> Link + Seraph + Morpheus + Switch
- Broker web UI and persistence -> Link + Trinity + Switch
- Azure auth flow changes -> Seraph + Morpheus + Switch
- Orleans evaluation or adoption -> Neo + Link + Morpheus + Switch

## Reviewer Defaults

- Switch reviews implementation changes for coverage and edge cases.
- Neo reviews architecture-impacting changes and cross-agent proposals.
- Morpheus may reject security or performance-sensitive work that does not meet resilience needs.

