## MODIFIED Requirements

### Requirement: One-shot reminders have one terminal settlement

Netclaw SHALL settle each one-shot reminder exactly once. The settlement SHALL end the one-shot. To end a one-shot, Netclaw SHALL record a reminder tombstone (see the [engineering glossary](../../../docs/spec/GLOSSARY.md#reminder-tombstone)) and then delete the definition and its schedule.

A one-shot SHALL end at the first of these events, with this outcome:

- a successful run: `Completed`
- a run with a denied tool call: `Failed`, with the denial text
- a failed run that reaches `FailurePauseThreshold`: `Failed`
- a failed run that Akka.Reminders does not retry (the negative acknowledgement replies `Failed` or `Expired`): `Failed`
- a fire after its `ExpiresAt`: `Expired`, and the reminder does not run
- a cancel: `Cancelled`
- a disabled one-shot whose fire time passed: `Cancelled`

Below the threshold, Netclaw SHALL keep a failed one-shot enabled so Akka.Reminders can retry it. A failed run below the threshold SHALL NOT end the one-shot.

A recurring reminder that reaches the threshold SHALL stay as a definition, disabled, with the `Failed` outcome. Netclaw SHALL NOT delete a recurring reminder on its own. Only an explicit delete command removes it.

The tombstone SHALL hold the id, the title, the audience, the fire time of the ended occurrence, the outcome, the end time, and the last error. A tombstone with the outcome `Failed` SHALL also hold the reminder instructions (the prompt). A tombstone with any other outcome SHALL NOT hold the instructions. No tombstone SHALL hold the delivery target, the delivery instructions, or the boundary. Netclaw SHALL delete the history and then the tombstone when the end time is more than 12 days old. It SHALL check at startup reconciliation and every 12 hours.

The failed count reported by `netclaw stats` SHALL count only enabled reminders with at least one consecutive failure.

#### Scenario: Successful one-shot is retained, then pruned

- **GIVEN** a one-shot reminder `check-pr-42` fires at 10:00
- **WHEN** its run succeeds and Netclaw settles the occurrence
- **THEN** the definition and the schedule of `check-pr-42` no longer exist
- **AND** a tombstone records the outcome `Completed` and the end time
- **AND** reminder history for `check-pr-42` returns the successful record
- **AND** Netclaw removes the history and the tombstone once the retention period has passed

#### Scenario: A current-session one-shot ends at admission

- **GIVEN** a one-shot reminder with `delivery_kind = current_session`
- **WHEN** the target session acknowledges the fire
- **THEN** the one-shot ends with the outcome `Completed`
- **AND** the end does not wait for the session turn

#### Scenario: A failed end keeps the prompt

- **GIVEN** a one-shot with the instructions "Check PR 42 and tell me when CI is green"
- **WHEN** the one-shot ends with the outcome `Failed`
- **THEN** the tombstone holds the instructions
- **AND** the tombstone does not hold the delivery target

#### Scenario: Other ends do not keep the prompt

- **GIVEN** three one-shots that end as `Completed`, `Cancelled`, and `Expired`
- **WHEN** Netclaw writes their tombstones
- **THEN** no tombstone holds the instructions

#### Scenario: Failed one-shot remains enabled for retry

- **GIVEN** a one-shot attempt fails for the second time
- **WHEN** Netclaw settles the failed attempt
- **THEN** the definition stays enabled
- **AND** no tombstone exists for the id
- **AND** reminder status shows the durable attempt state

#### Scenario: Poisoned one-shot remains inspectable

- **GIVEN** a one-shot has four consecutive failed attempts
- **WHEN** the fifth attempt fails
- **THEN** the definition and the schedule no longer exist
- **AND** the tombstone records the outcome `Failed` and the last error
- **AND** Netclaw emits the critical `reminder.auto_disabled` alert
- **AND** all five failure records stay readable through reminder history

#### Scenario: Akka.Reminders stops the retries

- **GIVEN** a one-shot attempt fails below the threshold
- **WHEN** the negative acknowledgement replies `Expired`
- **THEN** the one-shot ends with the outcome `Failed`

#### Scenario: A denied tool call ends the one-shot

- **GIVEN** a one-shot run calls a tool that authorization denies
- **WHEN** Netclaw settles the run
- **THEN** Netclaw acknowledges the occurrence and does not retry it
- **AND** the one-shot ends with the outcome `Failed` and the denial text
- **AND** the history row has the status `denied`

#### Scenario: An expired one-shot ends without a run

- **GIVEN** a restart-resume one-shot whose `ExpiresAt` is in the past
- **WHEN** its fire arrives
- **THEN** no execution starts
- **AND** the one-shot ends with the outcome `Expired`

#### Scenario: Failed reminders are kept but stop counting in stats

- **GIVEN** a cron reminder has four consecutive failed attempts
- **WHEN** the fifth attempt fails
- **THEN** the definition stays, disabled, with the outcome `Failed`
- **AND** no tombstone exists for the id
- **AND** the failed count in `netclaw stats` does not include it

#### Scenario: Reconciliation uses durable occurrence state

- **GIVEN** an enabled one-shot has a past fire time
- **WHEN** reconciliation finds no active schedule
- **THEN** reconciliation reads the durable occurrence state
- **AND** a terminal state ends the one-shot with the matching outcome
- **AND** a state that is not terminal leaves the occurrence to Akka.Reminders
- **AND** a missing state keeps the definition enabled for operator review

#### Scenario: The tombstone and the history are purged after 12 days

- **GIVEN** a one-shot ended at 10:00 on 1 October
- **WHEN** the sweep runs at 10:01 on 13 October
- **THEN** the history file and the tombstone no longer exist
- **AND** reminder history for the id replies "not found"

#### Scenario: The tombstone is kept inside the retention period

- **GIVEN** a one-shot ended at 10:00 on 1 October
- **WHEN** the sweep runs at 09:59 on 13 October
- **THEN** the tombstone and the history stay

### Requirement: Settlement write order supports recovery

Netclaw SHALL save a failed run and its poison count before it sends a negative acknowledgement. Netclaw SHALL not advance Akka state after a local save failure.

Netclaw SHALL write the tombstone of a one-shot before every other step of its end: before the acknowledgement, the negative acknowledgement after a threshold failure, the schedule cancel, and the definition delete. The only exception is a failure that is terminal only because of the negative acknowledgement reply. Then the tombstone SHALL come right after that reply.

If Netclaw cannot write the tombstone of a one-shot after a run that succeeded or had a denied tool call, it SHALL still acknowledge the occurrence. The acknowledgement records the durable status `Delivered`, so Akka.Reminders does not deliver the occurrence again, and the run does not repeat. Netclaw SHALL keep the definition, log an error, and emit the `reminder.settlement.failed` alert. The next startup reconciliation SHALL end the one-shot as `Completed` from the durable status.

If Netclaw cannot write the tombstone of any other end (a cancel, a failure at the threshold, or an expiry), it SHALL NOT acknowledge or negatively acknowledge the occurrence, cancel the schedule, or delete the definition. It SHALL emit a settlement failure alert. The acknowledgement lease SHALL stay the recovery path.

A crash at any step after the tombstone SHALL NOT run the one-shot again and SHALL NOT lose its outcome. Reconciliation SHALL repair one-shot terminal state after a post-acknowledgement process failure.

#### Scenario: Local failure state cannot be saved

- **GIVEN** an execution attempt fails
- **WHEN** Netclaw cannot save its poison state
- **THEN** Netclaw does not send a negative acknowledgement
- **AND** the Akka.Reminders acknowledgement timeout remains the recovery path

#### Scenario: The tombstone cannot be written after a successful or denied run

- **GIVEN** a one-shot run succeeds, or a tool call of the run is denied
- **WHEN** the tombstone write fails with an I/O error
- **THEN** Netclaw acknowledges the occurrence, and the durable status is `Delivered`
- **AND** the definition stays enabled
- **AND** Netclaw logs an error and emits the `reminder.settlement.failed` alert
- **AND** the one-shot does not run again, also after the acknowledgement lease time passes
- **AND** the next startup reconciliation ends the one-shot as `Completed` and deletes the definition

#### Scenario: The tombstone cannot be written for a cancel

- **GIVEN** a one-shot is scheduled
- **WHEN** a cancel cannot write the tombstone
- **THEN** the definition and the schedule stay
- **AND** Netclaw emits the `reminder.settlement.failed` alert
- **AND** the one-shot is not ended

#### Scenario: Process stops after the tombstone and before the acknowledgement

- **GIVEN** Netclaw wrote the `Completed` tombstone of a one-shot
- **AND** the process stopped before the acknowledgement
- **WHEN** Akka.Reminders delivers the occurrence again
- **THEN** no execution starts
- **AND** Netclaw acknowledges the occurrence and deletes the definition

#### Scenario: Process stops after successful acknowledgement

- **GIVEN** Netclaw acknowledged a terminal occurrence
- **AND** the process stopped before it wrote the tombstone
- **WHEN** the daemon starts
- **THEN** reconciliation reads the durable occurrence status
- **AND** a `Delivered` status ends the one-shot as `Completed`
- **AND** a `Failed`, `Expired`, or `Cancelled` status ends the one-shot as `Failed`

#### Scenario: Process stops after the tombstone and before the definition delete

- **GIVEN** a one-shot has a tombstone with the same fire time as its definition
- **WHEN** the daemon starts
- **THEN** reconciliation cancels the schedule and deletes the definition
- **AND** the tombstone stays unchanged

### Requirement: LLM self-cancellation of fulfilled reminders

Recurring reminders SHALL include prompt guidance telling the executing LLM to
call `cancel_reminder` when the reminder's purpose is permanently
fulfilled. This reuses the existing `cancel_reminder` tool
rather than introducing a separate completion tool — fewer tools means
less confusion for smaller models. For a recurring reminder, `cancel_reminder`
SHALL disable the reminder and keep its definition and history.

#### Scenario: LLM self-cancels a fulfilled recurring reminder

- **GIVEN** an enabled interval reminder fires and the LLM executes
- **AND** the LLM determines the task is permanently fulfilled
- **WHEN** the LLM calls `cancel_reminder` with the reminder's ID
- **THEN** the reminder is disabled and its schedule is cancelled
- **AND** the definition and the history stay
- **AND** future fires do not execute

#### Scenario: Prompt guidance includes reminder ID and cancellation instructions

- **GIVEN** a recurring (interval or cron) reminder definition
- **WHEN** the execution actor builds the prompt
- **THEN** the prompt includes guidance to call `cancel_reminder`
- **AND** the guidance includes the reminder's own ID

## ADDED Requirements

### Requirement: A cancel deletes a one-shot reminder

A cancel of a one-shot reminder SHALL end it with the outcome `Cancelled`. This applies to `cancel_reminder`, `netclaw reminder cancel`, and `DELETE /api/reminders/{id}` without `permanent`. Netclaw SHALL write the tombstone before it cancels the schedule. A cancel SHALL NOT stop a run that is active. A cancel of a recurring reminder SHALL disable it and keep it.

A cancel of an ended one-shot SHALL change nothing. It SHALL report that the reminder already ended, with the outcome and the end time. The audience gate SHALL apply to the cancel of an ended one-shot. Outside the caller's audience, the reply SHALL be "not found".

`netclaw reminder disable` SHALL stay a reversible pause for a one-shot. A disabled one-shot with a future fire time SHALL stay as a definition.

#### Scenario: Cancel deletes a one-shot

- **GIVEN** an enabled one-shot reminder `deploy-check` fires in two hours
- **WHEN** the agent calls `cancel_reminder` with `deploy-check`
- **THEN** the definition and the schedule no longer exist
- **AND** the tombstone records the outcome `Cancelled`
- **AND** the tool replies that the reminder was cancelled and deleted

#### Scenario: Cancel keeps a recurring reminder

- **GIVEN** an enabled interval reminder `ci-watch`
- **WHEN** the agent calls `cancel_reminder` with `ci-watch`
- **THEN** the definition stays, disabled
- **AND** no tombstone exists for `ci-watch`

#### Scenario: A process stop after the cancel tombstone does not run the reminder

- **GIVEN** a cancel wrote the tombstone of `deploy-check`
- **AND** the process stopped before the schedule cancel
- **WHEN** the daemon starts, or the fire arrives first
- **THEN** no execution starts
- **AND** the schedule and the definition no longer exist

#### Scenario: Cancel of an ended one-shot

- **GIVEN** `check-pr-42` ended as `Completed` at 10:00
- **WHEN** a caller in the same audience cancels `check-pr-42`
- **THEN** the reply says that the reminder already ended (completed at 10:00)
- **AND** the tombstone and the history do not change

#### Scenario: Cancel of an ended one-shot outside the audience

- **GIVEN** a Personal one-shot ended
- **WHEN** a Team caller cancels it
- **THEN** the reply is "not found", the same as for an id that never existed

#### Scenario: A paused one-shot with a future fire time stays

- **GIVEN** an operator ran `netclaw reminder disable` on a one-shot that fires tomorrow
- **WHEN** the sweep runs today
- **THEN** the definition stays, disabled
- **AND** `netclaw reminder enable` can schedule it again

#### Scenario: A paused one-shot ends when its fire time passes

- **GIVEN** a disabled one-shot whose fire time was yesterday
- **WHEN** the sweep runs
- **THEN** the one-shot ends with the outcome `Cancelled`

### Requirement: Settlement ends only the occurrence that it ran

Before it ends a one-shot, Netclaw SHALL check that the current definition is the one that ran. The fire time of the definition SHALL equal the due time of the settled occurrence, at millisecond precision. If the times differ, Netclaw SHALL record the history row and settle the occurrence, and it SHALL NOT change the definition. If no definition exists, Netclaw SHALL record the history row and settle the occurrence, and it SHALL NOT replace an existing tombstone.

#### Scenario: A watcher that sets itself again during its run keeps the new reminder

- **GIVEN** a `channel` one-shot `ci-poll` with the fire time 10:00 runs
- **AND** during the run, `set_reminder` replaces `ci-poll` with the fire time 10:10
- **WHEN** the 10:00 run succeeds and Netclaw settles it
- **THEN** the `ci-poll` definition with the fire time 10:10 stays enabled and scheduled
- **AND** no tombstone exists for `ci-poll`
- **AND** the history has a row for the 10:00 run

#### Scenario: The settled occurrence matches the definition

- **GIVEN** a one-shot with the fire time 10:00 runs
- **AND** nothing replaces its definition
- **WHEN** Netclaw settles the 10:00 run
- **THEN** the one-shot ends

#### Scenario: A cancel during the run wins

- **GIVEN** a one-shot run is active
- **AND** a cancel ends the one-shot as `Cancelled`
- **WHEN** the run succeeds and Netclaw settles it
- **THEN** the tombstone keeps the outcome `Cancelled`
- **AND** the history has a row for the run

### Requirement: A fire runs only the occurrence of the current definition

Before it runs, expires, or ends a one-shot for a fire, Netclaw SHALL check that the due time of the fire equals the fire time of the current definition, at millisecond precision. If the times differ, the occurrence belongs to an earlier definition of the same id. Netclaw SHALL NOT run it. Netclaw SHALL acknowledge that occurrence and log a warning. Netclaw SHALL NOT change the definition or its tombstone, and SHALL NOT cancel the schedule of the current definition.

#### Scenario: An occurrence of an earlier definition does not run early

- **GIVEN** a one-shot `ci-poll` with the fire time 10:10
- **AND** Akka.Reminders still holds an occurrence of an earlier `ci-poll` due at 10:00
- **WHEN** the 10:00 occurrence fires
- **THEN** no execution starts
- **AND** Netclaw acknowledges the 10:00 occurrence and logs a warning
- **AND** the `ci-poll` definition stays enabled, and its 10:10 schedule stays
- **AND** the run starts once, at 10:10

#### Scenario: An occurrence of the current definition runs

- **GIVEN** a one-shot `ci-poll` with the fire time 10:00
- **WHEN** the 10:00 occurrence fires
- **THEN** the execution starts

### Requirement: A tombstone that matches its definition wins

A tombstone SHALL match a definition when both have the same id, the definition is a one-shot, and both fire times are equal at millisecond precision. A matching tombstone SHALL mean that the one-shot ended.

- A fire for a definition with a matching tombstone SHALL NOT run. Netclaw SHALL acknowledge it, cancel the schedule, and delete the definition.
- A tombstone that does not match its definition SHALL be stale. Netclaw SHALL keep the definition, delete the stale tombstone, and log a warning.
- A fire for a one-shot whose tombstone file exists but cannot be read SHALL NOT run. Netclaw SHALL NOT acknowledge the occurrence. It SHALL keep the definition, the schedule, and the file. It SHALL log an error and emit the `reminder.tombstone.invalid` alert. The operator repairs or deletes the file, and the acknowledgement lease then returns the occurrence. A read of history or status still counts the unreadable file as absent (see `reminder-execution-history`).

#### Scenario: A matching tombstone blocks the run

- **GIVEN** a one-shot definition with the fire time 10:00
- **AND** a tombstone for the same id with the fire time 10:00
- **WHEN** the 10:00 fire arrives
- **THEN** no execution starts
- **AND** the definition no longer exists

#### Scenario: An unreadable tombstone stops the run

- **GIVEN** a one-shot definition with the fire time 10:00
- **AND** a tombstone file for the same id that Netclaw cannot read
- **WHEN** the 10:00 fire arrives
- **THEN** no execution starts
- **AND** Netclaw does not acknowledge the occurrence
- **AND** the definition, the schedule, and the file stay
- **AND** Netclaw logs an error and emits the `reminder.tombstone.invalid` alert

#### Scenario: A repaired tombstone lets the run happen

- **GIVEN** an unreadable tombstone stopped the 10:00 fire of a one-shot
- **WHEN** the operator deletes the file and the acknowledgement lease expires
- **THEN** Akka.Reminders delivers the occurrence again
- **AND** the execution starts once

#### Scenario: A stale tombstone does not block a newer definition

- **GIVEN** a one-shot definition with the fire time 11:00
- **AND** a tombstone for the same id with the fire time 10:00
- **WHEN** reconciliation runs
- **THEN** the definition stays enabled and scheduled
- **AND** the stale tombstone no longer exists
- **AND** Netclaw logs a warning that names the id

### Requirement: An ended one-shot id can be set again

A create or an update with the id of an ended one-shot SHALL start a new reminder. Netclaw SHALL first schedule the new occurrence. Then it SHALL delete the old history, delete the tombstone, and save the new definition. This applies to every write mode. If the schedule fails, the old history and the tombstone SHALL stay.

A tombstone outside the caller's audience SHALL block the id. The reply SHALL be the same "not found" reply as for a definition outside the caller's audience.

#### Scenario: Re-arm after the fire

- **GIVEN** `check-pr-42` ended as `Completed` and has one history row
- **WHEN** the agent calls `set_reminder` with the id `check-pr-42` and a new time
- **THEN** a new enabled definition with the new time exists
- **AND** the tombstone and the old history row no longer exist

#### Scenario: Re-arm discards the prompt of a failed one-shot

- **GIVEN** `check-pr-42` ended as `Failed` and its tombstone holds the instructions
- **WHEN** the agent calls `set_reminder` with the id `check-pr-42`
- **THEN** the tombstone no longer exists
- **AND** no file on disk holds the old instructions

#### Scenario: Restart recovery reuses an ended restart-resume id

- **GIVEN** a restart-resume one-shot ended
- **WHEN** restart recovery saves the same id with `CreateOnly`
- **THEN** the save succeeds and is not a conflict

#### Scenario: A tombstone outside the audience blocks the id

- **GIVEN** a Personal one-shot `nightly` ended
- **WHEN** a Team caller calls `set_reminder` with the id `nightly`
- **THEN** the save fails with "Reminder 'nightly' was not found."
- **AND** the tombstone and the history do not change

#### Scenario: A failed schedule keeps the old data

- **GIVEN** `check-pr-42` ended
- **WHEN** a re-arm gives a fire time in the past
- **THEN** the save fails
- **AND** the tombstone and the history stay

### Requirement: Read surfaces report an ended one-shot

Inside the retention period, the read surfaces SHALL report an ended one-shot to a caller in its audience:

- `list_reminders` and `netclaw reminder list` SHALL NOT show it.
- `netclaw reminder show <id>` and `GET /api/reminders/{id}` SHALL reply "not found" (HTTP 404, CLI exit code 1). The message SHALL name the outcome, the end time, and the time until which the history stays readable.
- `netclaw reminder status <id>` and `GET /api/reminders/{id}/status` SHALL reply with the outcome, the end time, the purge time, the last error, the durable occurrence status while it exists, and the recent history. For the outcome `Failed`, the reply SHALL also hold the reminder prompt. For the other outcomes, the reply SHALL NOT hold a prompt.
- `netclaw reminder enable|disable <id>` SHALL reply "not found" with the ended message.
- `run_reminder` SHALL reply with an error that says that the reminder ended.

The prompt of an ended one-shot SHALL be readable only through the status surfaces, and only for a caller inside the tombstone audience. `show`, `history`, `list`, and the agent tools SHALL NOT hold it.

Outside the caller's audience, every surface SHALL reply as for an id that never existed.

#### Scenario: Show on an ended one-shot

- **GIVEN** `check-pr-42` ended as `Completed` at 10:00 on 16 October
- **WHEN** the operator runs `netclaw reminder show check-pr-42`
- **THEN** the command exits with code 1
- **AND** the message says that the reminder ended (completed) at that time and names 28 October as the end of its history

#### Scenario: Status on an ended one-shot

- **GIVEN** `check-pr-42` ended as `Completed`
- **WHEN** the operator runs `netclaw reminder status check-pr-42`
- **THEN** the output shows the outcome `Completed`, the end time, and the recent history

#### Scenario: List does not show an ended one-shot

- **GIVEN** one enabled interval reminder and one ended one-shot
- **WHEN** the agent calls `list_reminders` with `filter = all`
- **THEN** the result shows only the interval reminder

#### Scenario: Status of a failed one-shot shows the prompt

- **GIVEN** `check-pr-42` ended as `Failed` with the instructions "Check PR 42 and tell me when CI is green"
- **WHEN** a caller inside the audience asks for its status
- **THEN** the reply holds the outcome `Failed`, the last error, and the instructions
- **AND** `netclaw reminder status check-pr-42` prints the instructions

#### Scenario: Status of a completed one-shot shows no prompt

- **GIVEN** `check-pr-42` ended as `Completed`
- **WHEN** a caller inside the audience asks for its status
- **THEN** the reply holds no instructions

#### Scenario: Status outside the audience

- **GIVEN** a Personal one-shot ended
- **WHEN** a Team caller asks for its status
- **THEN** the reply is "not found"

#### Scenario: The prompt of a failed one-shot stays inside its audience

- **GIVEN** a Personal one-shot ended as `Failed` and its tombstone holds the instructions
- **WHEN** a Team caller asks for its status, its history, or its details
- **THEN** every reply is "not found"
- **AND** no reply holds any part of the instructions

### Requirement: Reconciliation ends leftover one-shots

Startup reconciliation and the 12-hour sweep SHALL end every leftover one-shot definition. A leftover is a one-shot definition with a terminal outcome, or a disabled one-shot whose fire time has passed. The end time SHALL be the last update time of a definition with a terminal outcome. For a disabled one-shot without an outcome, the end time SHALL be the later of the last update time and the fire time. A leftover without a recorded outcome SHALL end as `Cancelled`, and its last error SHALL say that the outcome was not recorded. A leftover with the outcome `Failed` SHALL keep its instructions in its tombstone. The same pass SHALL purge each tombstone whose end time is more than 12 days old. The reconciliation log SHALL report the count of ended one-shots and the count of purged tombstones.

#### Scenario: Upgrade cleans old leftovers

- **GIVEN** an install with a `Completed` one-shot last updated 20 days ago, a `Failed` one-shot last updated 2 days ago, and a disabled one-shot without an outcome whose fire time was 30 days ago
- **WHEN** the new daemon starts
- **THEN** none of the three definitions exists
- **AND** the `Completed` and the disabled one-shot have no tombstone and no history
- **AND** the `Failed` one-shot has a tombstone that holds its instructions, and its history, until its 12 days pass
- **AND** the tombstone of a leftover that ended as `Completed` or `Cancelled` holds no instructions
- **AND** the reconciliation log line reports `ended_oneshots=3` and `purged_tombstones=2` (the leftovers that ended and were purged in the same pass count in both)

#### Scenario: Live reminders are not touched

- **GIVEN** an enabled one-shot with a future fire time, a disabled interval reminder, and a cron reminder with the outcome `Failed`
- **WHEN** reconciliation runs
- **THEN** all three definitions stay unchanged
- **AND** no tombstone exists for them
