## Why

Operators debugging reminders currently have to wait for the next scheduled fire
to verify permission, delivery, and execution fixes. Issue #1511 asks for a
daemon-backed way to run an existing reminder immediately so reminder DX issues
can be diagnosed with the same speed as webhook payload replay.

## Source PRDs

- `PRD-008` (Scheduling and Periodic Tasks): scheduled task management,
  execution history, and failure handling.
- `PRD-004` (CLI Onboarding and Configuration): operator-facing CLI management
  surfaces that talk to the daemon.
- `PRD-002` (Gateway Security Envelope): default-deny, operator-authorized
  control-plane actions, and fail-closed execution behavior.

## What Changes

- Add an operator-only immediate execution path for existing reminders:
  `netclaw reminder run <id>` backed by `POST /api/reminders/{id}/run`.
- The daemon asks `ReminderManagerActor` to run the persisted reminder definition
  through the normal `ReminderExecutionActor` pipeline without creating a durable
  Akka.Reminders schedule and without synthesizing a fake scheduler envelope.
- Manual execution leaves the reminder's existing schedule untouched: no
  `nextFire` mutation, no cron reschedule, and no one-shot consumption.
- Manual runs respect runtime safety gates: scheduling disabled, missing
  reminder, disabled reminder, expired recurring reminder, duplicate in-flight
  execution, and global concurrency exhaustion all fail loudly.
- Execution records carry whether the run was scheduled or manual so operators
  can interpret history and status output correctly.

In scope for MVP:

- CLI, REST API, actor protocol, execution origin tracking, history output, and
  tests for success/failure gates.
- Manual runs of enabled reminders only.

Out of scope for MVP:

- Running disabled reminders via `--allow-disabled`.
- Idempotency keys for retried HTTP/manual requests.
- Replaying arbitrary historical reminder payloads or overriding the persisted
  reminder definition at run time.
- A dashboard or Slack slash command for manual execution.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-scheduling`: immediate manual execution semantics, safety gates, and
  scheduled-vs-manual execution accounting.
- `netclaw-cli`: add `netclaw reminder run <id>` as an operator command that
  requires a running daemon.
- `reminder-execution-history`: execution records include the run source so
  manual diagnostic runs are distinguishable from scheduler fires.

## Impact

- **Actor protocol:** add an external `RunReminderNowCommand` with authorization
  context and response; add execution origin to the internal completion message.
- **Runtime:** `ReminderManagerActor` starts manual executions directly through
  `ReminderExecutionActor` with no Akka.Reminders envelope. Scheduled execution
  paths continue to use existing envelope ack/redelivery behavior.
- **REST API:** add `POST /api/reminders/{id}/run`, protected by the existing
  daemon authorization policy and restricted to Operator authority.
- **CLI:** add `netclaw reminder run <id>` and help text; daemon offline and
  non-success responses produce clear errors.
- **Persistence/history:** append `source` to new history records while retaining
  compatibility with existing JSONL records that lack the field.
- **Security:** no new tool grants or bypasses. Manual execution uses the stored
  reminder audience/boundary and the same delivery/tool policy as scheduled
  execution; the control-plane trigger is operator-only.
- **Operations:** operators can test permission and delivery changes immediately;
  manual failures are visible in history but do not count toward scheduled
  auto-disable thresholds.
