## Why

At a restart, the session logs `Tool approval requested` again for old approval prompts (issue #2525). The prompts are answered, cancelled, or lost. The log then says that approvals are pending when they are not. An operator, or a tool that counts these lines, gets a wrong picture. A real pending approval can hide among stale lines.

The cause is that journal replay calls the method that writes the log line for every `ToolApprovalRequested` event, also for an event that a later event closes. A child approval has no closing journal event. Thus replay re-opens each child approval, and an open entry blocks every snapshot until the session closes it in memory.

## What Changes

- Replay writes no `Tool approval requested` line. The session writes one line for each approval that is still open when recovery completes. That line carries `recovered=True`.
- The session closes the child approvals of a lost child owner in `RecoveryCompleted`, before it runs a stashed command. It no longer waits for the `RecoverChildRuns` self message.
- A new journal event, `ChildApprovalsClosed`, records that the session closed the approvals of one child run. Replay applies it and closes the entries without a log line.
- An old journal that has no `ChildApprovalsClosed` event still recovers. The recovery step in `RecoveryCompleted` closes those entries.
- No change to the prompt flow, the answer path, or the authorization rules.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `session-resume`: add requirements for the recovery approval log and for the durable close of child approvals.

## Impact

- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs`, `LlmSessionActor.Children.cs`, and `SessionToolWorkflow.Children.cs`.
- New persisted type `ChildApprovalsClosed` in `SessionProtocol.ChildRuns.cs`, with proto message `ChildApprovalsClosedProto` and manifest `cac-v1`.
- A new proto message uses its own field numbers. No existing field number, oneof case, or manifest changes. The numbers that open work uses stay free: `TurnRecordedProto` 8 to 10 and 20, `BackgroundChildRunV2Proto` 20, and snapshot 12, 20, and 21.
- `docs/architecture/tool-authorization.md` needs no change. No authorization context changes. The close event adds no decision owner, grant scope, or consent surface.
- A new focused mutation target for the child approval table. It joins the `child-owner-and-report` CI group.
