## Why

Source PRDs: `PRD-001`, `PRD-009`.

Netclaw must let operators interrupt an autonomous tool loop from Slack without
turning that interruption into extra context for the still-active tool
continuation. Production session `D0AC6CKBK5K/1780689744.134609` showed that
mid-loop user corrections were buffered, drained into the same turn, and then
followed by another tool-enabled LLM continuation, producing repeated preamble +
tool-call cycles instead of a fresh response to the latest user instruction.

## What Changes

- Treat real user input received during an active tool-call continuation as an
  interruption boundary, not as mid-loop context for the current turn.
- Close or abandon any open assistant tool calls before the interrupting message
  starts a fresh turn, preserving provider-valid tool-call/tool-result history.
- Preserve existing buffering behavior for non-interruptible states such as
  compaction and restart drain.
- Ignore stale LLM/tool callbacks from abandoned work so they cannot restart the
  interrupted loop.
- Keep Slack preamble delivery behavior unchanged; repeated preambles are fixed
  by stopping the old loop, not by suppressing user-visible text output.

In scope for MVP: session actor turn-boundary behavior, persisted history
integrity, regression tests, and eval coverage for interrupting tool loops.

Out of scope for MVP: manual UI controls for cancelling turns, new transport
protocol fields, global preamble suppression, or changes to approval policy.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `turn-loop-governance`: user input during an active tool loop interrupts the
  current tool-enabled continuation instead of being appended inside it.
- `netclaw-session`: persisted session history must remain well-formed when an
  in-flight tool batch is interrupted by a new user message.
- `session-state-machine`: `Processing` phase must distinguish normal tool-loop
  continuation from user-message interruption and must not allow stale callbacks
  from abandoned work to continue the prior turn.

## Impact

- Affected code:
  - `src/Netclaw.Actors/Sessions/LlmSessionActor.cs`
  - `src/Netclaw.Actors/Sessions/ActiveToolBatchTracker.cs` if additional stale
    batch identity is needed
  - session actor integration tests under `src/Netclaw.Actors.Tests/Sessions/`
  - eval cases covering tool-loop interruption behavior
- Security impact: positive. Authorized operator corrections can stop risky or
  repetitive tool activity instead of being folded into the same tool loop.
- Operational impact: Slack users can interrupt harmful or stale autonomous work
  mid-turn; interrupted tool calls are recorded as abandoned rather than silently
  orphaned.
- Dependency impact: no new dependencies or external APIs.
