## Why

A one-shot reminder stays as a definition after it fires or after someone cancels it (issue #2526). On one live install, 97 of 189 definitions were spent one-shots: 26 completed, 1 failed, and 70 disabled with no recorded outcome. These leftovers make `netclaw reminder list` and `list_reminders` noisy, and they hide what is really scheduled.

The owner decided the behavior: a one-shot reminder deletes itself after it fires or after it is cancelled. This change defines how. It traces to PRD-008 SCHED-002 (schedule persistence), SCHED-005 (task management and history), SCHED-007 (failure handling), and PRD-001 FR-012 (chat-driven scheduling).

## What Changes

- A one-shot reminder **ends** at its first terminal event. The terminal events are a settled run (success or a denied tool call), a failure that Netclaw does not retry, a cancel, and a fire after its `ExpiresAt`. The reminder manager then deletes the definition file and the Akka.Reminders schedule.
- At each end, the manager first writes a small durable **reminder tombstone**. The tombstone keeps the id, title, audience, fire time, outcome, end time, and last error. A `Failed` tombstone also keeps the reminder prompt, so an operator can create the reminder again. The other outcomes do not keep the prompt. No tombstone keeps the delivery target. Only a caller inside the tombstone audience can read the prompt, through `netclaw reminder status`.
- The execution history file stays with the tombstone for the existing 12-day retention. After that, the manager deletes the history and then the tombstone. `netclaw reminder history`, `netclaw reminder status`, and `get_reminder_history` still work for an ended one-shot in that window. The audience gate reads the audience from the tombstone.
- **BREAKING**: `cancel_reminder`, `netclaw reminder cancel`, and `DELETE /api/reminders/{id}` delete a one-shot (with a `Cancelled` tombstone). They no longer keep a disabled one-shot definition. A recurring reminder keeps the current cancel behavior: Netclaw disables it and keeps it.
- **BREAKING**: a one-shot that reaches `FailurePauseThreshold`, or that Akka.Reminders stops retrying, ends with a `Failed` tombstone. A run with a denied tool call also ends as `Failed`. Netclaw no longer keeps a disabled `Failed` one-shot definition. Such a definition could not be enabled again, because its fire time is in the past. The `Failed` tombstone keeps the prompt for the 12-day period.
- `ReminderTerminalOutcome` gets two values: `Cancelled` and `Expired`. Only a tombstone uses them.
- Settlement ends only the occurrence that it ran. If a reminder with the same id was set again during the run, the new definition stays.
- Re-arm: `set_reminder` (or a create) with the id of an ended one-shot deletes the old history and the tombstone, and then saves the new definition. This keeps the current rule for a `Completed` one-shot. A tombstone outside the caller's audience blocks the id until the tombstone is purged, the same as a definition outside the caller's audience.
- `netclaw reminder status <id>` on an ended one-shot prints the outcome, the end time, the purge time, and the history. For `Failed`, it also prints the prompt.
- `netclaw reminder show <id>` on an ended one-shot exits with code 1 and prints the outcome, the end time, and the date until which the history stays readable.
- Upgrade: the startup reconciliation and the existing 12-hour sweep end every leftover one-shot definition. A leftover is a one-shot with a terminal outcome, or a disabled one-shot whose fire time has passed. The end time of a leftover is its last update time, so a leftover that is older than 12 days is purged in the same pass. No new CLI command is added.
- `CLI disable` stays a reversible pause. A disabled one-shot with a future fire time stays. When its fire time passes while it is disabled, the sweep ends it as `Cancelled`.
- The `netclaw-operations` skill (`references/scheduling.md`) and the `run-reminder` skill describe the new lifecycle. The tool texts of `set_reminder`, `cancel_reminder`, and `run_reminder` and the CLI help change to match.

### In scope

- The one-shot lifecycle, the tombstone, its retention, the read surfaces, re-arm, crash repair, and the upgrade sweep.
- Restart-resume reminders (`restart-resume-*`). They are one-shots with `ExpiresAt`, so they follow the same rules.

### Out of scope

- A stop condition for an interval or cron watcher that serves one job. This needs a separate issue.
- A `netclaw reminder prune` command (owner decision, 16 Oct) or a `list --ended` view.
- Cleanup of orphan history files that have no definition and no tombstone.
- A change to the retention period (12 days) or to `FailurePauseThreshold`.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-scheduling`: one-shot terminal settlement becomes "end and delete with a tombstone"; cancel deletes a one-shot; settlement ends only the occurrence that it ran; the settlement write order puts the tombstone first; read surfaces report an ended one-shot; the upgrade sweep ends leftovers; LLM self-cancellation text matches the current recurring cancel behavior.
- `reminder-execution-history`: history of an ended one-shot stays readable through its tombstone for the retention period; a permanent delete removes the tombstone too.

## Impact

- `src/Netclaw.Actors/Reminders`: `ReminderManagerActor` (one end path, sweep, reconcile, fire handler, cancel, save, reads), `ReminderDefinitionStore` (tombstone files in a subfolder), `ReminderProtocol` (`ReminderTombstone`, outcome values, response fields), `CancelReminderTool`, `SetReminderTool`, `RunReminderTool`, `GetReminderHistoryTool`.
- `src/Netclaw.Daemon/Reminders/ReminderEndpointRouteBuilderExtensions.cs`: cancel, get, status, and history replies for an ended one-shot.
- `src/Netclaw.Cli/Reminder/ReminderCommand.cs`: `show`, `status`, `cancel` output and help text.
- System skills: `netclaw-operations` (`references/scheduling.md`, version bump) and `run-reminder` (version bump).
- `docs/spec/GLOSSARY.md`: add "reminder tombstone".
- Disk layout: a new folder `~/.netclaw/schedules/reminders/ended/`. No config schema change. No Akka.Reminders schema change.

### Security impact

- The audience gate stays the single owner (`ReminderManagerActor.CanAccessAudience`). It reads the tombstone audience for an ended one-shot. A caller outside that audience gets the same "not found" reply as for an id that never existed.
- The tombstone never keeps the delivery target. It keeps the prompt only for `Failed`, for at most 12 days, and behind the same audience gate as the history. Before this change, the definition kept the prompt with no limit. After this change, an ended reminder keeps less data on disk than before.
- No authorization context changes. `docs/architecture/tool-authorization.md` needs no update.

### Operational impact

- After the upgrade, `reminder list` shows only live reminders and paused one-shots with a future fire time.
- An operator loses the disabled `Failed` one-shot definition. The critical `reminder.auto_disabled` alert, the tombstone, and 12 days of history replace it.
- A downgrade is safe. An older binary does not read the `ended/` folder, and it ignores history files that have no definition.
