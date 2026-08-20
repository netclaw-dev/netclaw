## Context

Akka.Reminders already persists each occurrence attempt, deadline, failure reason, and terminal state. Netclaw acknowledges channel and no-delivery reminders before their LLM session completes.

A failed one-shot then has no active Akka occurrence. Reconciliation treats that absence as completion and deletes the definition and history.

## Goals / Non-Goals

**Goals:**

- Use Akka.Reminders as the occurrence retry source of truth.
- Preserve failed one-shots until retry success or a terminal failure.
- Preserve the separate Netclaw poison threshold for the complete reminder.
- Keep old reminder JSON files and the Akka.Reminders 0.6.0 schema compatible.

**Non-Goals:**

- Add a recurring catch-up queue.
- Add a durable ingress queue for all session messages.
- Change reminder trust or tool-policy derivation.

## Decisions

### Akka.Reminders owns occurrence retry state

Netclaw uses the entity-bound `IReminderClient.NackAsync` method for a known failure. It uses `GetOccurrenceStatusAsync` for retry and terminal diagnostics.

Netclaw does not copy an occurrence attempt count or retry timestamp into its definition file.

### Netclaw owns reminder-level poison state

Netclaw persists `ConsecutiveFailures` in each reminder definition. Each failed execution attempt increments this value, and a success resets it.

This value spans recurring occurrences. Akka.Reminders resets its attempt count for each new occurrence.

### The reminder manager coordinates settlement

`ReminderManagerActor` passes the envelope to `ReminderExecutionActor` for all delivery kinds. The child reports its outcome and waits for manager acceptance.

The manager saves the history and reminder state before it settles a known failure. It then sends the negative acknowledgement.

The manager resets the reminder failure count before it acknowledges a success. It records one-shot completion only after a successful acknowledgement.

The manager replies to the child after settlement. The child stops only after this reply, so DeathWatch cannot replace an accepted result.

An actor crash before an outcome leaves the occurrence unacknowledged. The manager records the crash and attempts a negative acknowledgement without risking its own lifecycle.

### Capacity does not transfer occurrence ownership

Netclaw does not retain a blocked Akka.Reminders envelope in an in-memory queue. A queue wait could consume the 70-minute acknowledgement lease.

Netclaw negatively acknowledges a blocked one-shot. Akka.Reminders then owns its retry delay and attempt budget.

Netclaw acknowledges and skips a blocked reminder-series occurrence. This rule prevents a catch-up queue and preserves the latest-only series policy.

Netclaw ignores an exact duplicate of the active occurrence. The active execution remains the sole settlement owner.

### One-shot completion uses a soft delete

A successful one-shot sets `Enabled` to false and records `TerminalOutcome.Completed`. A poison or terminal one-shot records `TerminalOutcome.Failed`.

The explicit delete command remains the only normal hard-delete path. It also deletes the history file.

### Reconciliation uses durable state

Reconciliation never uses a past fire time or a missing active schedule as proof of success. It retains disabled one-shots and restores an enabled one-shot when durable state permits another attempt.

### Timeouts remain bounded

Netclaw sets the Akka acknowledgment timeout to 70 minutes. The execution actor applies a one-hour absolute attempt limit and keeps its 20-minute inactivity limit.

Known failures use negative acknowledgement and do not wait for the acknowledgment timeout.

Netclaw starts an attempt only when the remaining envelope lease exceeds the maximum attempt duration plus a settlement margin.

## Risks / Trade-offs

- **A daemon crash can delay retry for 70 minutes.** The long lease prevents duplicate LLM work during a valid one-hour attempt.
- **At-least-once delivery can duplicate work.** The occurrence identity remains `(Entity, Key, DueTimeUtc)` and session identifiers use that stable due time.
- **Netclaw and Akka.Reminders use separate stores.** Ordered writes and reconciliation provide convergence without a cross-store transaction.
- **A custom Akka storage provider can lack status queries.** Netclaw uses the official SQLite provider and fails loudly if the capability is absent.
- **Old JSON files lack the new fields.** Serializer defaults preserve active state and a zero failure count.

## Migration Plan

1. Release Akka.Reminders 0.7.0 with the additive delivery-control API.
2. Upgrade Netclaw to both 0.7.0 packages.
3. Load old reminder JSON files with default values.
4. Keep the existing SQLite schema without a migration.
5. Roll back Netclaw by restoring the prior binary. The added JSON fields remain harmless to older readers.

## Open Questions

None.
