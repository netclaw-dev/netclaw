## Context

See `proposal.md` for the reason. This section gives only the current state that shapes the design.

- `ReminderManagerActor` is the single writer of reminder state. It handles one message at a time, also for `ReceiveAsync` handlers. All decisions below run in this actor.
- Definitions are files `{RemindersDirectory}/{escapedId}.json`. `ReminderDefinitionStore.List()` reads every top-level `*.json` file. It deletes a file that does not parse, and it rejects a file without trust fields.
- History is `{RemindersDirectory}/{escapedId}.history.jsonl`, keyed only by the id. `ReminderHistoryStore` has no other index.
- Every history read and status read gets the audience from the definition. If the definition is missing, the read replies "not found". So, if the manager deletes only the definition, the history becomes unreachable, and it has no audience to gate it.
- After #2402, a successful one-shot is saved as `Enabled = false`, `TerminalOutcome = Completed`. The manager prunes it with its history after `TerminalRetention` (12 days). `TerminalRetention` is also the Akka.Reminders `PruneOlderThan`, so the durable occurrence status lives for the same 12 days.
- A `Failed` one-shot, a cancelled one-shot (`cancel_reminder` = disable), a one-shot with a denied tool call, and an expired one-shot (`Expired_reminder_disabled_on_fire_without_executing`) stay forever as disabled definitions. These are the 71 leftovers in the issue that the current prune does not touch.
- After D2 (#2511), a `current_session` run ends at admission. The manager acknowledges the envelope when the session sends `CommandAck`. A `channel` or `none` run ends when the isolated turn ends. "Fired" in this design means "the manager settled the occurrence".
- Restart-resume reminders are one-shots with `ExpiresAt` and a random id (`ReminderIdGenerator.Generate("restart resume")`). `RestartRecoveryService` registers them with `CreateOnly`.
- The coordination stack (`feature/coordination-child-owner`) changes no reminder manager code. It changes `netclaw-operations/SKILL.md`, so the skill version bump in this change can conflict when the two merge.

## Goals / Non-Goals

**Goals:**

- One end path for every one-shot terminal event, with one durable commit point.
- No crash window that runs a one-shot twice after its outcome is durable, and no crash window that loses the outcome.
- The history of an ended one-shot stays readable, behind the same audience gate, for the retention period.
- The upgrade cleans up existing leftovers without an operator step.

**Non-Goals:**

- Interval and cron reminders. Their cancel, failure, and expiry behavior does not change.
- A user-visible list of ended reminders.
- Exactly-once execution. Akka.Reminders stays at-least-once until the manager settles the occurrence.

## Decisions

### D1. The end of a one-shot writes a tombstone, then deletes the definition

The manager writes a **reminder tombstone** for each ended one-shot. The tombstone is the durable record that the one-shot ended. Its fields:

| Field | Purpose |
|---|---|
| `id` | The reminder id. |
| `title` | Text for `show`, `status`, and the tool replies. |
| `audience` | The audience gate for history, status, cancel, and re-arm. |
| `fireAtMs` | The due time of the ended occurrence, in Unix milliseconds. It matches the tombstone to a definition (D4). |
| `outcome` | `Completed`, `Failed`, `Cancelled`, or `Expired`. |
| `endedAtMs` | The end time from `TimeProvider`. Retention counts from this time. |
| `lastError` | The failure or denial text, or the reason for a repair. Null for a clean success. |
| `instructions` | The reminder prompt. Set only when the outcome is `Failed`. Absent for the other outcomes (D1a). |

The tombstone never keeps the delivery target, the delivery instructions, or the boundary. A tombstone never runs, so it does not need them.

The file uses the JSON options of the definition store (web defaults: camel-case names, enum values as names). Example for a `Failed` outcome:

```json
{
  "id": "check-pr-42",
  "title": "Check PR 42",
  "audience": "Team",
  "fireAtMs": 1792144800000,
  "outcome": "Failed",
  "endedAtMs": 1792144812000,
  "lastError": "model endpoint is not available",
  "instructions": "Check PR 42 and tell me when CI is green."
}
```

Where: `{RemindersDirectory}/ended/{escapedId}.json`. `ReminderDefinitionStore` owns the folder, the lock, the path containment check, and the atomic write (temp file `{path}.tmp`, then rename; the same temp name as a definition write). The folder is a constraint, not a style choice: `List()` reads every top-level `*.json` file. A tombstone at the top level would be parsed as a definition, then deleted as invalid or rejected as legacy. An older binary also enumerates only the top level, so a downgrade ignores the folder.

Alternatives:

- Keep the disabled definition and hide it from `list` (the current #2402 shape, extended). Rejected: the owner decided that the definition is deleted. The leftovers would only move out of view, and every reader of `List()` would still see them.
- Delete the definition and keep only the history. Rejected: the history has no audience, so no read could be gated. An ungated read is a disclosure; a read that always fails makes the kept history useless.
- Append a terminal row to the history file. Rejected: `HistoryRecord` is a wire shape for the CLI, REST, and `get_reminder_history`. A row of a different kind changes three consumers.
- A new store class. Rejected under "reuse before you add": `ReminderDefinitionStore` already has the folder, the lock, the containment check, and the atomic write.

### D1a. Only a `Failed` tombstone keeps the prompt

Owner answer (16 Oct): a `Failed` tombstone keeps the reminder prompt, so an operator can create the reminder again.

- `Failed`: the tombstone keeps `instructions`. The reminder did not do its job, and the operator needs the text to try again.
- `Completed`, `Cancelled`, `Expired`: the tombstone does not keep the prompt. A `Completed` reminder did its job. A `Cancelled` reminder was unwanted. An `Expired` reminder missed its window on purpose. Nobody needs the text again, so Netclaw does not keep it on disk longer than it has to.
- The prompt is data of the tombstone audience. Only a caller inside that audience can read it, with the same rule as the history (`CanAccessAudience`). Outside the audience, the reply is "not found" and shows no field of the tombstone.
- The only read surface for the prompt is `reminder status` (`GET /api/reminders/{id}/status`, field `ended.instructions`). `show`, `history`, `list`, and every agent tool do not show it.
- The prompt goes away with the tombstone: at the 12-day purge, at a permanent delete, and at a re-arm of the same id (D6).
- A tombstone that Netclaw cannot read has no prompt for any reader (D7, Risks).

Example, positive: a one-shot ends `Failed` after five failures. `netclaw reminder status check-pr-42` prints the prompt. The operator copies it into a new `set_reminder` call.

Example, negative: a Personal one-shot ends `Failed`. A Team caller asks for its status. The reply is "not found". It does not show the prompt, and it does not show that the id exists.

Alternatives:

- Keep the prompt for every outcome. Rejected: it keeps more text on disk for a use case that only `Failed` has.
- Keep the prompt in the history file. Rejected: `HistoryRecord` is a wire shape (see D1).

### D2. Which events end a one-shot, and with which outcome

| Event | Outcome | Envelope action | Owner |
|---|---|---|---|
| A run succeeds (any delivery kind; `current_session` at admission) | `Completed` | ack | manager, `SettleSuccessfulExecutionAsync` |
| A run has a denied tool call | `Failed`, `lastError` = the denial | ack | manager, `SettleDeniedExecutionAsync` |
| A failed run reaches `FailurePauseThreshold` | `Failed` | nack | manager, `SettleFailedExecutionAsync` |
| A failed run gets `Failed` or `Expired` from the nack | `Failed` | (nack already sent) | manager, `SettleFailedExecutionAsync` |
| A fire arrives after `ExpiresAt` | `Expired` | ack, no run | manager, fire handler |
| `cancel_reminder`, CLI `cancel`, REST cancel | `Cancelled` | none | manager, cancel handler |
| A disabled one-shot reaches its fire time (CLI `disable` earlier) | `Cancelled` | none | manager, sweep |

A failure below the threshold is not an end. The definition stays enabled, and Akka.Reminders retries the occurrence.

A denied run ends as `Failed` because the one-shot did not do its job. The history row keeps the status `denied`. The denial still does not count toward `FailurePauseThreshold`.

A `Failed` one-shot is deleted too. Its fire time is in the past, so `enable` could never schedule it again. The critical `reminder.auto_disabled` alert, the channel notice, the tombstone, and the history give the operator the same facts as the kept definition.

CLI `disable` stays a pause. A disabled one-shot with a future fire time can be enabled again, so it stays a definition. When its fire time passes, it can never run, so the sweep ends it.

### D3. One ordered end path

All events in D2 use one manager routine. Schematic (the audience gate and the logs are left out):

```
EndOneShot(def, outcome, lastError, endedAt, envelopeAction)      // manager, actor-local call
  1. store.SaveTombstone(tombstone(def, outcome, lastError, endedAt))  // durable: commit point
       // instructions are copied from def only when outcome = Failed (D1a)
       on I/O error -> alert "reminder.settlement.failed"; stop here.
                       No nack, no schedule cancel, and no delete. The lease expiry is the recovery path.
                       Exception (owner decision, 17 Oct): after a success or a denied run, ack the
                       envelope anyway and keep the definition. The ack writes the durable status
                       Delivered. The startup reconciliation ends the one-shot as Completed, and the
                       run does not repeat. Log an error.
  2. envelopeAction: ack | nack | none                              // durable in Akka.Reminders
  3. CancelScheduleOnly(def.Id)                                     // durable, idempotent
  4. _skipCounts.Remove(id); _deniedAlerts.Remove(id)               // actor-local
  5. store.Delete(def.Id)                                           // durable
```

The tombstone comes first because it is the only step that records the outcome. Each later step can fail or stop, and D4 finishes it.

- Success: today the manager acks first and saves `Completed` after. If the ack replies `Error`, the definition stays enabled, and the redelivery runs the reminder again. With the tombstone first, the redelivery finds the tombstone and does not run (D4). The rule "save local state before the ack" stays true.
- Threshold failure: the tombstone replaces the save of the disabled `Failed` definition. Then the nack, then the schedule cancel.
- A nack that replies `Failed` or `Expired`: the manager learns of the end only from the nack reply, so the nack comes before step 1. A crash between the nack and step 1 leaves an enabled definition with a past fire time and a terminal durable occurrence status. The startup reconciliation already maps that status to an end.
- Cancel: the tombstone comes before the schedule cancel. If the schedule cancel came first and the process stopped, the reconciliation would see an enabled definition with a future fire time and no schedule. It would restore the schedule, and the cancelled reminder would run.
- Expired fire: the tombstone comes before the ack. If the ack came first and the process stopped, the reconciliation would read `Delivered` and record `Completed` for a reminder that did not run.

### D4. A matching tombstone wins over its definition

A tombstone **matches** a definition when both have the same id, the definition is a one-shot, and both fire times are equal at millisecond precision. The manager compares `ToUnixTimeMilliseconds()` values, the same precision as `ReminderFireKey`.

- A fire for a definition with a matching tombstone does not run. The manager does steps 3 to 5 and acks the envelope.
- A fire for a one-shot whose tombstone file exists but cannot be read does not run, because the fire decides whether a run happens (owner decision, 17 Oct). The manager does not ack. It keeps the definition, the schedule, and the file, logs an error, and raises `reminder.tombstone.invalid`. A read still treats the file as absent.
- A fire whose envelope due time differs from the definition fire time (at millisecond precision) belongs to an earlier definition of the same id. The manager does not run it. It acks the envelope, logs a warning, and does not touch the definition or the schedule.
- A fire for an id with no definition does not run (current behavior). If a tombstone exists, the manager logs at Info ("fired after it ended"), not at Error.
- The reconciliation and the sweep finish a definition that has a matching tombstone (steps 3 to 5).
- A tombstone that does not match its definition is stale. The definition is newer, so it wins. The manager deletes the stale tombstone and logs a warning. Only a manual file edit can make this state, because a re-arm deletes the tombstone before it saves (D6).

Owner: the manager. Data: durable (both files). The match is a pure function of the two records, so a focused mutation target can cover it.

### D5. Settlement ends only the occurrence that it ran

Before it ends a one-shot, the settlement checks that the current definition is the one that ran: the definition fire time equals the envelope due time (`execution.Envelope.DueTimeUtc`), at millisecond precision.

- Equal: the settlement ends the one-shot (D3).
- Not equal: a `set_reminder` with the same id replaced the definition during the run. The settlement appends the history row and settles the old envelope. It does not change the new definition.
- No definition: a cancel ended the one-shot during the run. The settlement appends the history row and settles the envelope. It does not overwrite the `Cancelled` tombstone.

This also fixes a current fault. Today the settlement reads the definition again after the run, and `RetainCompletedOneShotAsync` disables whatever definition it finds. A watcher that sets itself again during its own `channel` run is disabled, and with this change it would be deleted. A `current_session` run usually settles before its turn calls `set_reminder`. The two messages come from different senders, so the order is not guaranteed, and the check covers both orders.

### D6. Re-arm of an ended id

`set_reminder` uses `Upsert`, and `RestartRecoveryService` uses `CreateOnly`. For both, an id that has only a tombstone is free. Order, in `HandleSaveAsync`:

```
tomb = store.GetTombstone(id)                      // durable read
if tomb is not null and not CanAccessAudience(caller, tomb.audience)
    reply NotFound (same text as for a definition outside the audience); stop
validate audience and boundary                    // unchanged
schedule the new occurrence in Akka.Reminders     // a failure stops here; old data stays
historyStore.DeleteHistory(id)                    // durable
store.DeleteTombstone(id)                         // durable
store.Save(newDefinition)                         // durable
```

- The old history and the old tombstone (with the `Failed` prompt, if it has one) are discarded, as today for the history of a `Completed` one-shot. A new reminder can have a lower audience than the old one, so the old history must not become readable under the new audience.
- Today a `Failed` one-shot blocks its id under `CreateOnly`. After this change, no `Failed` one-shot definition exists, so the id is free.
- A crash after the schedule and before the save leaves an Akka.Reminders schedule with no definition. The caller got no success reply. The reconciliation cancels the orphan schedule, and a fire before that does not run (no definition).
- A tombstone outside the caller's audience blocks the id until the sweep purges the tombstone. The caller cannot tell this apart from a definition outside its audience. This keeps one rule for the id space.

### D7. Read surfaces

| Surface | Ended one-shot in the caller's audience | Outside the audience |
|---|---|---|
| `list_reminders`, `reminder list` | Not shown | Not shown |
| `reminder show <id>`, `GET /api/reminders/{id}` | 404. The message names the outcome, the end time, and the purge time. The CLI exits with code 1. | 404 "not found" |
| `reminder status <id>`, `GET .../status` | 200 with `ended` data (outcome, end time, purge time, last error, and for `Failed` the prompt), the durable occurrence status (until Akka prunes it), and the recent history | 404 |
| `reminder history <id>`, `get_reminder_history` | The history rows | 404 / "not found" |
| `cancel_reminder`, `reminder cancel`, `DELETE /api/reminders/{id}` | "Reminder 'x' already ended (completed at ...)." Nothing changes. HTTP 200, CLI exit code 0. | "not found" (HTTP 404) |
| `reminder enable` / `disable` | 404 with the ended message | (operator only) |
| `reminder delete <id>` | Deletes the history, then the tombstone | (operator only) |
| `run_reminder` | Error: the reminder ended; set it again to run it | "not found" |

`show` exits with code 1 because the definition is gone. A script that reads `.instructions` must not get a different JSON shape with exit code 0. Example CLI output:

```
[FAIL] Reminder 'check-pr-42' ended (completed) at 2026-10-16 10:00:00Z and its definition was deleted.
       Its history stays readable until 2026-10-28 10:00:00Z: netclaw reminder history check-pr-42
```

Protocol changes (all additive, exact shapes so that the consumers and the tests agree):

```csharp
public sealed record ReminderEndedInfo(
    ReminderTerminalOutcome Outcome,   // Completed | Failed | Cancelled | Expired
    DateTimeOffset EndedAt,
    DateTimeOffset PurgeAt,            // EndedAt + TerminalRetention
    string? LastError,
    string? Instructions);             // non-null only for Failed (D1a)

GetReminderResponse(ReminderInfo? Reminder, ReminderEndedInfo? Ended = null)
ReminderStatusResponse(..., IReadOnlyList<HistoryRecord> RecentHistory, ReminderEndedInfo? Ended = null)
ReminderCancelledResponse(ReminderId Id, bool Found, ReminderEndedInfo? AlreadyEnded = null,
                          string? ErrorMessage = null, bool Deleted = false)
ReminderStateResponse.ErrorMessage            // the ended message for enable and disable
```

- `ReminderCancelledResponse.Deleted` is true when this cancel ended a one-shot. The tool, the REST route, and the CLI use it to pick "cancelled and deleted" or "cancelled (disabled)".
- `ReminderCancelledResponse.AlreadyEnded` is set (with `Found = true`) when the id is an ended one-shot in the caller's audience.
- `ReminderStatusResponse.Found` is true for an ended one-shot in the caller's audience. `Enabled` is false and `Executing` is false.
- `ReminderHistoryResponse` is unchanged (`Found` is true for a tombstone in scope).
- The REST `ReminderStatusDto` gets `ended` (the same fields as `ReminderEndedInfo`, camel-case). The REST `GET /{id}` 404 body keeps the shape `{ "error": "..." }`; the message names the outcome, the end time, and the purge time.
- REST `ended.outcome` is the outcome name as a string (`"Failed"`). `endedAt` and `purgeAt` are ISO 8601 timestamps.
- The REST `GET /{id}` 404 message and the CLI `show` text: `Reminder '{id}' ended ({outcome in lower case}) at {EndedAt:u} and its definition was deleted. Its history stays readable until {PurgeAt:u}: netclaw reminder history {id}`.
- The cancel replies of the tool, the REST route, and the CLI: `Reminder '{id}' cancelled and deleted.` (a one-shot), `Reminder '{id}' cancelled (disabled).` (a recurring reminder), and `Reminder '{id}' already ended ({outcome in lower case} at {EndedAt:u}). Nothing changed.` (an ended one-shot).
- The CLI `status` text for an ended one-shot has the lines `Ended:               {outcome in lower case} at {EndedAt:u} (history stays until {PurgeAt:u})`, `Last error:          {text}` (when set), and `Prompt:              {text}` (only for `Failed`).
- The CLI writes `status` and `cancel` output through the `output` and `error` writers of `ReminderCommand.RunAsync`, as `show` and `history` do now. A test can then read it without a change of `Console`.

### D8. Sweep, reconciliation, and upgrade

The existing `PruneTerminalReminders` timer (every 12 hours) and the startup reconciliation run one sweep. Schematic:

```
Sweep(now)                                                    // manager; reads durable state
  defs  = store.List(); tombs = store.ListTombstones()
  for each one-shot def:
    matching tomb            -> finish steps 3..5 (D4)
    stale tomb               -> delete the tomb, warn (D4)
    def.TerminalOutcome = o  -> EndOneShot(def, o, "repaired: settled before the definition was deleted",
                                           endedAt = def.UpdatedAt, none)
    !def.Enabled and def.FireAt <= now
                             -> EndOneShot(def, Cancelled, "disabled when its fire time passed",
                                           endedAt = max(def.UpdatedAt, def.FireAt), none)
  for each tomb with endedAt + TerminalRetention < now:
    historyStore.DeleteHistory(id); store.DeleteTombstone(id)   // history first: the tomb anchors it

Reconcile (startup only) adds, before the sweep:
  enabled one-shot with FireAt <= now and no schedule:
    durable occurrence status Delivered            -> EndOneShot(Completed, endedAt = CompletedAtUtc ?? now)
    Failed | Expired | Cancelled                   -> EndOneShot(Failed,    endedAt = CompletedAtUtc ?? now)
    no status                                      -> keep for operator review (current behavior)
```

The same rules repair a crash and migrate an old install:

- An old `Completed` or `Failed` one-shot has `TerminalOutcome`. It ends with that outcome. A `Failed` leftover keeps its prompt in the tombstone (D1a). A `Completed` leftover does not.
- An old cancelled, denied, or expired one-shot is disabled with a past fire time. It ends as `Cancelled`, and its `lastError` reads "The outcome was not recorded." (the tests match the words "not recorded").
- The end time is the last update time, so a leftover older than 12 days loses its tombstone and its history in the same pass. A newer leftover keeps its history for the rest of the 12 days.

The reconciliation log line gets two counts: `ended_oneshots` and `purged_tombstones`.

### D9. No new prune command

Recommendation: do not add `netclaw reminder prune`.

- The startup reconciliation and the 12-hour sweep do the same work with no operator step.
- A new verb needs a REST route, a daemon command, a dry-run mode, docs, and tests. It gives no power that the sweep does not have.
- The cleanup that the owner asked for now must run on the current release, which has no such verb.

For the live install now, the operator can use the existing commands. Review the first output before you run the second command. The `jq` filter also matches a paused one-shot with a future fire time, because `list` does not show the fire time. `reminder delete` also deletes the history.

```bash
# 1. Dry run: one-shots that are disabled (spent, cancelled, or paused).
netclaw reminder list --json \
  | jq -r '.[] | select(.schedule == "runs once" and .enabled == false)
           | [.id, (.terminalOutcome // "none"), .title] | @tsv'

# 2. Delete them.
netclaw reminder list --json \
  | jq -r '.[] | select(.schedule == "runs once" and .enabled == false) | .id' \
  | while read -r id; do netclaw reminder delete "$id"; done
```

### D10. Tool, CLI, and skill text

- `cancel_reminder` description: "Cancel a reminder by its ID. A one-shot reminder is deleted, and its run history stays readable for 12 days. A recurring reminder is disabled and kept. Use list_reminders to find reminder IDs."
- `cancel_reminder` reply: "Reminder 'x' cancelled and deleted." (one-shot) or "Reminder 'x' cancelled (disabled)." (recurring).
- `set_reminder` reply for a one-shot: "Runs once. Next execution: {time}. The reminder deletes itself after it runs or after you cancel it."
- CLI help: `cancel <id>  Cancel a reminder (deletes a one-shot; disables a recurring reminder)`.
- `netclaw-operations` `references/scheduling.md`:
  - Table row: "Stop a reminder (a one-shot is deleted; a recurring reminder is disabled)".
  - Replace the `cancel_reminder` **disables** paragraph with: "`cancel_reminder` deletes a one-shot reminder and disables a recurring reminder. A disabled recurring reminder keeps its definition, so `netclaw reminder enable <id>` can start it again. `netclaw reminder delete <id>` permanently deletes any reminder and its history."
  - Replace the one-shot paragraph (from "A one-shot reminder stays enabled" to "keeps its id until you delete it.") with: "A one-shot reminder stays enabled while an occurrence can retry. It deletes itself after it runs, after its failures reach the limit, after its expiry time, or after it is cancelled. Netclaw then keeps a small record of the end (the outcome and the time) and the run history for 12 days. `netclaw reminder status <id>` and `netclaw reminder history <id>` work in that period. `netclaw reminder show <id>` reports that the reminder ended. `list_reminders` does not show it. To run the same one-shot again, call `set_reminder` with the same ID. This starts a new reminder and discards the old history. Netclaw never deletes a recurring reminder on its own. Only `netclaw reminder delete <id>` removes it."
  - Bump `metadata.version` in `SKILL.md`.
- `run-reminder` `SKILL.md`: add "A one-shot reminder deletes itself after it runs or after it is cancelled. Test a one-shot before its fire time. After that, `run_reminder` reports that the reminder ended." Bump `metadata.version`.

### Owners and data scope

| Decision | Owner | Data |
|---|---|---|
| A one-shot ends (D2, D3) | `ReminderManagerActor` | durable: tombstone, definition, Akka.Reminders occurrence |
| Matching tombstone wins (D4) | `ReminderManagerActor` | durable read of two files; call-local result |
| The settled occurrence is the current one (D5) | `ReminderManagerActor` | actor-local `ActiveReminderExecution.Envelope` and durable definition |
| Audience gate for an ended id, including the `Failed` prompt (D1a, D6, D7) | `ReminderManagerActor.CanAccessAudience` | durable tombstone audience |
| Retention purge (D8) | `ReminderManagerActor` sweep | durable; `TimeProvider` time |
| Skip counts, denied alerts | `ReminderManagerActor` | actor-local, removed at the end |

## Risks / Trade-offs

- [A `Failed` tombstone holds the prompt on disk for up to 12 days] → The definition held the same text before this change, with no limit. The audience gate protects the read (D1a). The purge, the permanent delete, and the re-arm remove it. Only `Failed` keeps it.
- [A tombstone outside the caller's audience blocks the id for up to 12 days] → The same rule applies to a live definition. Ids are usually unique per job. A Personal caller can still re-arm it.
- [A re-arm discards the old history, so a self-re-arming watcher keeps one run of history] → This is the current rule for a `Completed` one-shot. A recurring reminder is the better shape for a watcher; a stop condition for watchers is a separate issue.
- [`RestartRecoveryService` retries a manifest after a failed delete and re-creates an ended restart reminder] → The id is free after the end, as it is today for a `Completed` one-shot. D2 (#2511) makes a restart-resume message that resumes nothing a no-op.
- [A one-shot that is down for more than 12 days loses its Akka occurrence status] → The reconciliation keeps it for operator review, as today.
- [A corrupt tombstone file] → `GetTombstone` logs an error and raises the `reminder.tombstone.invalid` alert. For a read, the id is then treated as having no tombstone, so the audience gate falls back to "no data", not to an open read. For a fire, the one-shot does not run (D4). The file stays for the operator. A re-arm overwrites the path.
- [A cancel during an active run] → The run continues, as today. The `Cancelled` tombstone stays, and the settlement adds only the history row (D5).
- [Skill version conflict with the coordination stack] → Rebase this change after the stack, and take the higher version plus one.

## Migration Plan

1. Deploy the new binary. The startup reconciliation ends the leftovers and purges the old ones (D8). No config or schema change.
2. Rollback: an older binary ignores `ended/` and the history files without a definition. It does not re-create the deleted definitions. The tombstones and history stay on disk until the new binary runs again, or the operator deletes the `ended/` folder.
3. Before the release, the operator can clean the live install with the D9 commands.

## Resolved Questions

Owner answers, 16 Oct 2026:

1. A `Failed` tombstone keeps the prompt, so an operator can create the reminder again. Only `Failed` keeps it (D1a). A caller outside the audience cannot read it.
2. A run with a denied tool call ends as `Failed` (D2).
3. No `netclaw reminder prune` command (D9).
