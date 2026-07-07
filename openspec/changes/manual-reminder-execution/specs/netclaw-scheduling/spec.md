## ADDED Requirements

### Requirement: Operator-triggered immediate reminder execution

The reminder manager SHALL support an operator-triggered immediate execution of
an existing enabled reminder. Immediate execution SHALL run the persisted
`ReminderDefinition` through the normal reminder execution pipeline and SHALL be
marked with execution source `manual`.

Immediate execution SHALL NOT create, mutate, or cancel an Akka.Reminders
schedule. It SHALL NOT synthesize a fake scheduler envelope. It SHALL NOT mutate
the reminder's next scheduled fire time, reschedule cron reminders, or consume a
one-shot reminder's scheduled occurrence.

If a one-shot scheduled occurrence fires while a manual execution for the same
reminder is already active, the manager SHALL queue exactly one scheduled
execution behind the active manual run, acknowledge the scheduler envelope, and
run the queued scheduled occurrence after the manual run completes. That queued
scheduled execution SHALL retain source `scheduled` and SHALL apply normal
one-shot completion behavior.

The manager SHALL reject immediate execution before dispatch when scheduling is
disabled, the request lacks operator authorization context, the reminder does
not exist, the reminder is disabled, the reminder is an expired recurring
reminder, the same reminder is already executing, or the global reminder
execution concurrency limit is full. Rejected immediate executions SHALL return a
structured response with a clear reason and SHALL NOT append execution history.

Manual execution completion SHALL append normal execution history, but manual
failure and success SHALL NOT modify the scheduled consecutive-failure counter or
trigger scheduled auto-disable behavior.

#### Scenario: Manual execution starts without changing the schedule

- **GIVEN** an enabled interval reminder has a scheduled next fire time
- **WHEN** an operator triggers immediate execution for that reminder
- **THEN** the manager starts a `ReminderExecutionActor` using the persisted reminder definition
- **AND** the execution source is `manual`
- **AND** no Akka.Reminders schedule is created, cancelled, or rescheduled for the manual run
- **AND** the reminder's next scheduled fire time remains unchanged

#### Scenario: Manual execution of one-shot does not consume the reminder

- **GIVEN** an enabled one-shot reminder has a future scheduled fire time
- **WHEN** an operator-triggered immediate execution completes successfully
- **THEN** the reminder definition remains enabled
- **AND** the original one-shot schedule remains available for its future scheduled fire

#### Scenario: Scheduled one-shot fires while manual execution is active

- **GIVEN** an enabled one-shot reminder is executing from an operator-triggered manual run
- **WHEN** the reminder's scheduled one-shot occurrence fires
- **THEN** the manager acknowledges the scheduler envelope
- **AND** queues one scheduled execution behind the active manual run
- **AND** does not dispatch the scheduled occurrence concurrently with the manual run
- **WHEN** the manual run completes
- **THEN** the queued scheduled execution starts with source `scheduled`
- **AND** the one-shot reminder is disabled after the scheduled execution completes

#### Scenario: Manual execution rejects disabled reminder

- **GIVEN** a disabled reminder definition exists
- **WHEN** an operator triggers immediate execution for that reminder
- **THEN** the manager rejects the request before dispatch
- **AND** no execution actor is started
- **AND** no execution history is appended

#### Scenario: Manual execution rejects missing authorization context

- **GIVEN** an enabled reminder definition exists
- **WHEN** immediate execution is requested without operator authorization context
- **THEN** the manager rejects the request before dispatch
- **AND** no execution actor is started
- **AND** no execution history is appended

#### Scenario: Manual execution rejects duplicate in-flight reminder

- **GIVEN** a reminder is already executing from a scheduled or manual run
- **WHEN** an operator triggers immediate execution for the same reminder
- **THEN** the manager rejects the request as already executing
- **AND** the request is not queued
- **AND** the skipped-duplicate counter for scheduled fires is not incremented

#### Scenario: Manual execution rejects when global concurrency is full

- **GIVEN** `MaxConcurrentExecutions` reminder executions are already active
- **WHEN** an operator triggers immediate execution for another enabled reminder
- **THEN** the manager rejects the request as busy
- **AND** the request is not queued in the scheduled deferred queue

#### Scenario: Manual execution does not affect scheduled failure accounting

- **GIVEN** a reminder has scheduled consecutive-failure count greater than zero
- **WHEN** a manual execution for that reminder succeeds or fails
- **THEN** the scheduled consecutive-failure count remains unchanged
- **AND** manual failure does not auto-disable the reminder

#### Scenario: CurrentSession manual execution does not use scheduler ack or redelivery

- **GIVEN** a reminder persisted with `Delivery.Kind = CurrentSession`
- **WHEN** an operator triggers immediate execution
- **THEN** the execution actor dispatches the trusted session turn using the persisted delivery target
- **AND** no Akka.Reminders envelope is passed to the execution actor
- **AND** no scheduler ack or scheduler redelivery is attempted for the manual run
