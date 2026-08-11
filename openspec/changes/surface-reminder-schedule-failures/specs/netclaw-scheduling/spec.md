## MODIFIED Requirements

### Requirement: Failure handling and guardrails

Netclaw's reminder manager SHALL track consecutive failures per reminder via the
persisted `ConsecutiveFailures` count and SHALL auto-pause a reminder when the
count reaches an internal `FailurePauseThreshold` constant. Both execution
failures and scheduling failures SHALL increment the same count. A successful
execution SHALL reset the failure count to zero. This is the recovery path for
both failure kinds: a successful execution proves the reminder both scheduled and
ran. The manager SHALL NOT reset the count on a successful reschedule alone,
because the post-fire reschedule of a cron reminder runs before that occurrence
executes, so a reset there would erase pending execution-failure accumulation.
Paused reminders SHALL remain persisted with `status: "paused"` and SHALL be
visible via `netclaw reminders list`.

`FailurePauseThreshold` is not operator-configurable — it lives as an
`internal const` on `ReminderManagerActor`. `Akka.Reminders` applies its
own separate retry budget (`MaxDeliveryAttempts`, library default) to
envelope delivery; Netclaw's auto-pause threshold is set strictly below
the library's default so the Netclaw-side pause fires first in practice
and operators see a `paused` reminder in `netclaw reminders list` before
the library would mark an occurrence terminally failed. If either
default changes in a way that breaks this ordering, add back a single
operator knob.

A scheduling failure is a failure to compute or install the next occurrence at an
unattended reschedule site — the post-fire reschedule of a recurring reminder, or
the startup reconcile restore loop. Causes include an unresolvable `CRON_TZ` time
zone, a cron expression with no future occurrence, and an uninitialized reminder
client. When a scheduling failure happens at an unattended site, the manager
SHALL:

- increment the reminder's `ConsecutiveFailures` count;
- emit an `OperationalAlert.ReminderScheduleFailed` (Warning);
- when the count reaches `FailurePauseThreshold`, disable the reminder, emit an
  `OperationalAlert.ReminderAutoDisabled` (Critical), and post a channel notice.

The manager SHALL NOT silently evaluate an unresolvable time zone in UTC. The
manager SHALL NOT silently skip a failed reschedule. A wrong-time fire is worse
than a missed fire.

The create or update path (`set_reminder`) SHALL continue to return scheduling
errors synchronously to the caller and SHALL NOT additionally emit a
scheduling-failure alert, because that failure is already visible to the user.

The reminder manager SHALL allow any number of reminder executions to run
concurrently — there is no execution cap, because each execution already has a
one-hour absolute timeout and Akka.Reminders owns failure retry. The manager
SHALL enforce a per-execution timeout (`ExecutionTimeoutSeconds`, internal
const on `ReminderExecutionActor`).

#### Scenario: Consecutive failures auto-pause task

- **GIVEN** a scheduled task has failed N times in a row where N equals
  `FailurePauseThreshold`
- **WHEN** the Nth failure is reported to `ReminderManagerActor`
- **THEN** the task status is set to `paused`
- **AND** the Akka timer for the task is cancelled
- **AND** a log event is emitted naming the reminder and the failure count
- **AND** the reminder remains in `tasks.json` with `status: "paused"`

#### Scenario: Successful execution resets failure counter

- **GIVEN** a scheduled task has failed twice
- **WHEN** the next execution succeeds
- **THEN** the internal failure count for that reminder is reset to zero
- **AND** subsequent failures start counting from zero again

#### Scenario: Reminders run concurrently without an execution cap

- **GIVEN** several reminders are already executing
- **WHEN** another reminder fires
- **THEN** the new reminder starts executing immediately
- **AND** no occurrence is skipped or deferred for capacity reasons

#### Scenario: Execution timeout enforced

- **GIVEN** a reminder execution exceeds the per-execution timeout
- **WHEN** the timeout fires
- **THEN** the execution is cancelled and reported as a failure
- **AND** the failure is counted toward `FailurePauseThreshold`

#### Scenario: Scheduling failure on post-fire reschedule is surfaced

- **GIVEN** a recurring reminder fires
- **AND** its next occurrence cannot be computed, for example the `CRON_TZ` zone
  no longer resolves
- **WHEN** the manager attempts the post-fire reschedule
- **THEN** the reminder's `ConsecutiveFailures` count is incremented
- **AND** an `OperationalAlert.ReminderScheduleFailed` alert is emitted
- **AND** the current occurrence still executes

#### Scenario: Scheduling failure during reconcile is surfaced

- **GIVEN** an enabled reminder whose next occurrence cannot be computed at
  startup
- **WHEN** the reconcile restore loop attempts to reschedule it
- **THEN** the reminder's `ConsecutiveFailures` count is incremented
- **AND** an `OperationalAlert.ReminderScheduleFailed` alert is emitted
- **AND** the reminder is not silently skipped

#### Scenario: Consecutive scheduling failures auto-disable the reminder

- **GIVEN** a reminder has failed to schedule N-1 times where N equals
  `FailurePauseThreshold`
- **WHEN** the Nth scheduling failure is reported
- **THEN** the reminder is disabled
- **AND** an `OperationalAlert.ReminderAutoDisabled` critical alert is emitted
- **AND** a channel notice is posted

#### Scenario: Recovery — a successful execution resets scheduling failures

- **GIVEN** a reminder has failed to schedule twice
- **AND** its schedule later recovers so the reminder fires again
- **WHEN** that occurrence executes successfully
- **THEN** the consecutive-failure count is reset to zero

#### Scenario: A successful reschedule alone does not reset the counter

- **GIVEN** a cron reminder has a non-zero consecutive-failure count
- **WHEN** the post-fire reschedule of an occurrence succeeds
- **THEN** the count is NOT reset by the reschedule
- **AND** only a later successful execution resets it

#### Scenario: Unresolvable zone never falls back to UTC

- **GIVEN** a cron reminder with an unresolvable `CRON_TZ` zone
- **WHEN** a reschedule is attempted at an unattended site
- **THEN** no occurrence is scheduled
- **AND** the reminder is not evaluated in UTC
- **AND** the failure is surfaced through the count and a `ReminderScheduleFailed`
  alert

#### Scenario: One bad startup does not mass-disable reminders

- **GIVEN** many enabled reminders whose schedules all fail once at startup
- **AND** `FailurePauseThreshold` is greater than one
- **WHEN** the reconcile restore loop runs
- **THEN** each affected reminder's count is incremented by one
- **AND** no reminder is disabled solely because of a single startup failure
- **AND** an `OperationalAlert.ReminderScheduleFailed` alert is emitted per
  affected reminder
