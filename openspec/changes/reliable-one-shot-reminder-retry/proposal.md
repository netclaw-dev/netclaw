## Why

PRD-008 requires durable failure records and an automatic pause after repeated failures. Netclaw now deletes a failed one-shot before Akka.Reminders can retry it.

## What Changes

- Netclaw will acknowledge every reminder only after successful execution and required delivery.
- Netclaw will report a known failure through the Akka.Reminders negative acknowledgement API.
- A failed one-shot will remain enabled while another occurrence attempt is pending.
- A completed or terminally failed one-shot will use a soft delete.
- Netclaw will persist its reminder-level consecutive failure count.
- The reminder manager will coordinate local state and Akka occurrence settlement.
- Netclaw will not keep Akka.Reminders envelopes in an in-memory catch-up queue.
- Reconciliation will use durable occurrence state and will never infer success from a past due time.
- Reminder status output will show the durable occurrence attempt and terminal outcome.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-scheduling`: Change acknowledgement, retry, poison-reminder, one-shot retention, and reconciliation requirements.
- `reminder-execution-history`: Retain execution history for soft-deleted one-shot reminders.

## Impact

- **In scope:** PRD-008 reminder execution, reminder status, reconciliation, the definition store, and Akka.Reminders 0.7.0 integration.
- **Out of scope:** A catch-up queue for recurring occurrences and a general durable ingress queue for all sessions.
- **Security:** The change keeps the current trust context and tool policy for every retry.
- **Operations:** Operators can inspect failed one-shots and retry state after a daemon restart.
- **Compatibility:** Existing reminder JSON files load with default values. The Akka.Reminders 0.6.0 database schema remains valid.
