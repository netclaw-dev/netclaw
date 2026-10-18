## Why

At a restart, the session logs `Tool approval requested` again for old approval prompts (issue #2525). The prompts are answered, cancelled, or lost. The log then says that approvals are pending when they are not. An operator, or a tool that counts these lines, gets a wrong picture. A real pending approval can hide among stale lines.

The cause is that journal replay calls the method that writes the log line for every `ToolApprovalRequested` event, also for an event that a later event closes. Replay also re-opens each child approval. Today the session closes a child approval in memory only, in the live cancel and terminal callbacks and in `RecoverChildren`. The journal already holds the records that justify the close (`CancellationRequested` and `TerminalRecorded`), but replay does not use them. An open entry blocks every snapshot.

## What Changes

- Replay writes no `Tool approval requested` line. The session writes one line for each approval that is still open when recovery completes. That line carries `recovered=True`.
- One rule closes the child approvals of a run: its `CancellationRequested` record or its `TerminalRecorded` record. Live code and journal replay both apply it. Replay then rebuilds a closed table from the journal. This also repairs old journals.
- `RecoveryCompleted` closes the child approvals that remain after replay, before it runs a stashed command. A child owner never survives a restart. It no longer waits for the `RecoverChildRuns` self message.
- No new journal event, proto message, or serializer manifest. The persisted format does not change.
- No change to the prompt flow, the answer path, or the authorization rules.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `session-resume`: add requirements for the recovery approval log and for the close of child approvals.

## Impact

- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs`, `LlmSessionActor.Children.cs`, and `SessionToolWorkflow.Children.cs`.
- No persisted type changes. No proto field number, oneof case, or manifest changes. The numbers that open work uses stay free.
- `docs/architecture/tool-authorization.md` needs no change. No authorization context changes. The change adds no decision owner, grant scope, or consent surface.
- A new focused mutation target for the child approval close rule. It joins the `child-owner-and-report` CI group.
