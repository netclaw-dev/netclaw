## Context

Reminder executions are currently driven by Akka.Reminders envelopes delivered to
`ReminderManagerActor`. The manager resolves the persisted `ReminderDefinition`,
applies runtime gates, starts `ReminderExecutionActor`, and uses envelope ack / redelivery
semantics for scheduled `CurrentSession` deliveries.

Issue #1511 needs a debugging path that executes the same persisted reminder now
without waiting for the next scheduled fire. The path must be fast for operators
but must not disturb the scheduler state, bypass stored trust context, or make
manual diagnostic failures count as scheduled production failures.

## Goals / Non-Goals

**Goals:**

- Add an operator CLI/API path to run an existing enabled reminder immediately.
- Reuse the existing reminder execution actor and persisted reminder definition.
- Keep durable schedules and `nextFire` unchanged.
- Preserve scheduled envelope ack/redelivery behavior for real scheduler fires.
- Distinguish manual and scheduled runs in manager accounting and history.

**Non-Goals:**

- No `--allow-disabled` behavior in this change.
- No idempotency key or retry de-duplication for repeated CLI/API calls.
- No arbitrary prompt override or one-off unsaved reminder execution.
- No dashboard, Slack slash command, or TUI surface.
- No unattended approval escalation. If a manual or scheduled autonomous run hits
  an approval gate today, this change may surface that failure sooner, but it does
  not route the prompt back to a live operator channel.

## Decisions

### D1. Manual runs enter through an explicit manager command

Add `RunReminderNowCommand(ReminderId, Authorization)` to the external reminder
actor protocol. The daemon endpoint sends this command to `ReminderManagerActor`;
the manager rejects missing authorization context, loads the persisted definition,
and starts execution directly.

**Rationale:** This keeps the control-plane request inside the actor that already
owns duplicate execution, concurrency, expiry, and definition lookup. It avoids
touching Akka.Reminders storage for a diagnostic action.

**Alternative rejected:** create a temporary Akka.Reminders schedule or fake
`ReminderEnvelope`. A temporary schedule can collide with the durable reminder
key or alter scheduler state; a fake envelope would make ack/redelivery behavior
lie about scheduler-owned state.

### D2. Execution origin is first-class

Add an execution source (`scheduled` or `manual`) to the execution start path,
internal completion message, and history record. Scheduled fires use
`scheduled`; CLI/API run-now uses `manual`.

**Rationale:** The manager must know whether completion should update scheduled
failure counters and one-shot lifecycle. History also needs this signal so
operators do not mistake a diagnostic run for a scheduled fire.

### D3. Manual runs fail fast rather than queueing

When the same reminder is active or the global concurrency limit is full, manual
run requests are rejected with a structured busy response. They are not added to
the scheduled deferred queue.

**Rationale:** The CLI command is a debugging action; a later queued run gives
operators less certainty and can mask the state they were trying to inspect.
Scheduled fires keep the existing deferred/duplicate semantics.

### D4. Manual completion does not change scheduled failure accounting

Manual successes and failures are written to history but do not increment, reset,
or trip the scheduled consecutive-failure counter. Manual one-shot completion does
not disable the reminder.

**Rationale:** Diagnostic attempts should not auto-disable production reminders or
erase production failure state. Operators can inspect the manual history entry to
confirm a fix without mutating scheduled accounting.

### D5. REST endpoint uses existing daemon auth plus Operator classification

`POST /api/reminders/{id}/run` is placed under the existing authorized reminder
route group and rejects callers that do not map to `PrincipalClassification.Operator`.

**Rationale:** Running a reminder can invoke tools and send messages under stored
trust context. Triggering it is therefore an operator control-plane action, not a
general authenticated-user action.

## Risks / Trade-offs

- **HTTP retry can duplicate a manual run after a fast completion** -> Accept for
  MVP; add explicit run idempotency later if operators need scripted retries.
- **A scheduled fire arriving during a manual run is skipped by the existing
  duplicate guard** -> Documented trade-off: non-overlap is safer than parallel
  reminder execution. Status exposes skipped scheduled fires.
- **History files with old records lack source** -> Default missing source to
  `scheduled` during read and add a regression test.
- **Manual run of CurrentSession has no scheduler redelivery** -> Intentional;
  failures surface through the manual response/history path and can be retried by
  the operator.

## Migration Plan

No config or reminder definition migration is required. Existing history JSONL
records remain readable and default to `scheduled` source when the field is
absent. Rollback leaves new records with an extra `source` JSON field; older code
that ignores unknown JSON properties can continue reading the other fields.

## Open Questions

- Should a follow-up add `--allow-disabled` for operators who intentionally want
  to test a paused reminder without re-enabling it?
- Should scripted/API users get an optional idempotency key after the first CLI
  workflow proves useful?
