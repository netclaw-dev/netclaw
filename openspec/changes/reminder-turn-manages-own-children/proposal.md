## Why

A reminder turn cannot check or cancel a child run of its own session. The call to `check_agent_run` returns "This child run is not available to the requester". The same call works in a TUI turn of the same session (issue #2524).

The cause is the requester rule. A reminder fire has sender `reminder-system` and principal `VerifiedAutomation`. A person's TUI turn has sender `local` and principal `Operator`. The two turns differ in two of the six requester facts.

The owner decided on 16 October 2026: a person who can start a reminder can also manage a sub-agent. A reminder turn may check and cancel the child runs of its own session. On 18 October 2026 the owner added two answers: a person turn of the same session may manage a child run that a reminder turn started, and the owner accepts the shared-channel effect.

## What Changes

- A reminder turn can check and cancel a child run when the session, audience, boundary, and channel type match the run. The comparison ignores the sender ID and the principal classification when the requester or the turn that started the run is a reminder turn.
- A person turn can check and cancel a child run that a reminder turn started in the same session, with the same audience, boundary, and channel type. The sender of the person does not matter.
- Two turns that are not reminder turns keep the six-fact rule. A person turn of another sender in the same session stays denied for a run that a person started.
- The skills `agent-coordination`, `netclaw-operations`, and `run-reminder` state the rule. The test PR lists the exact sentences. The implementer changes the skill files.
- The architecture document `docs/architecture/tool-authorization.md` describes the new comparison and its owner.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `background-subagent-runs`: add the requirement "A reminder turn shares child runs with the people of its own session".

## Impact

- `TurnContext` gets one new check for child control. `LlmSessionActor.ControlChild` calls it. `HasSameRequester` keeps its meaning, because input batching also uses it.
- No persistence change. The check reads the `SourceKind` and the principal of the recorded turn context. The run keeps its `OriginalContext`.
- No configuration change and no schema change.
- A new focused mutation target covers the new comparison (`TOOLING.md`).
- Accepted consequence: in a shared channel session, a reminder that one participant wrote can manage a child run that another participant started. Any participant can manage a child run that a reminder started. The owner accepted this on 18 October 2026. See `design.md`.
