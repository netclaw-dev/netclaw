## Why

A reminder turn cannot check or cancel a child run of its own session. The call to `check_agent_run` returns "This child run is not available to the requester". The same call works in a TUI turn of the same session (issue #2524).

The cause is the requester rule. A reminder fire has sender `reminder-system` and principal `VerifiedAutomation`. A person's TUI turn has sender `local` and principal `Operator`. The two turns differ in two of the six requester facts.

The owner decided on 16 October 2026: a person who can start a reminder can also manage a sub-agent. A reminder turn may check and cancel the child runs of its own session.

## What Changes

- A reminder turn can check and cancel a child run when the session, audience, boundary, and channel type match the run. The comparison ignores the sender ID and the principal classification for a reminder turn.
- Every other requester keeps the six-fact rule. A person turn of another sender in the same session stays denied.
- A person turn cannot check or cancel a child run that a reminder turn started. This rule does not change. The proposal states it so that no reader assumes the opposite.
- The skills `agent-coordination`, `netclaw-operations`, and `run-reminder` state the rule. The test PR lists the exact sentences. The implementer changes the skill files.
- The architecture document `docs/architecture/tool-authorization.md` describes the new comparison and its owner.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `background-subagent-runs`: add the requirement "A reminder turn manages the child runs of its own session".

## Impact

- `TurnContext` gets one new check for child control. `LlmSessionActor.ControlChild` calls it. `HasSameRequester` keeps its meaning, because input batching also uses it.
- No persistence change. The check reads the `SourceKind` and the principal of the recorded turn context. The run keeps its `OriginalContext`.
- No configuration change and no schema change.
- A new focused mutation target covers the new comparison (`TOOLING.md`).
- Known consequence: in a shared channel session, a reminder that one participant wrote can manage a child run that another participant started. The reminder shares the session. See `design.md`.
