# Switch — Issue #2 Message Envelope Contract

- Chosen contract shape: `MessageEnvelope<TPayload>` in `src\SquadScout.Contracts\Messages\` with shared JSON settings in `SessionMessageSerializer.DefaultOptions` so broker, app, and functions can all serialize the same camelCase/string-enum wire format.
- Acknowledgement remains a top-level envelope concern via `AcknowledgedSequence`; heartbeat payloads only carry liveness metadata (`ReplayRequested`, `ExpectedIntervalSeconds`, `SenderInstanceId`) to avoid multiple sources of truth for sequencing state.
- Replay responses explicitly publish both the requested replay range and the currently available replay window (`AvailableFromSequence`, `AvailableToSequence`, `GapDetected`) so reconnect overflow becomes an explicit recovery path.
- Backward compatibility rule for contract version `1`: additive optional members and new message types are allowed within the major version; renames, removals, or sequence/ack semantic changes require a major version bump.
