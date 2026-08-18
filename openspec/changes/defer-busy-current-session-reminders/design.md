## Context

CurrentSession reminders reuse the origin session and its channel binding. Each binding writes trusted reminder input into the same session queue as user input.

The session accepts queued input during an active turn. Its tool-loop drain then copies only message content into the active turn and discards reminder identity.

The channel binding registers a delivery observer before queue admission. The observer waits for a `TurnCompleted.SourceReminderId` that the active human turn cannot produce.

Akka.Reminders 0.7.0 has no separate defer API. Its `NackAsync` contract schedules durable retry with bounded exponential backoff and consumes one delivery attempt.

## Goals / Non-Goals

**Goals:**

- Reject CurrentSession reminder admission while the target session has an active turn.
- Use Akka.Reminders for durable delay and retry ownership.
- Keep transient deferrals out of Netclaw failure history, alerts, and poison counts.
- Convert retry-budget exhaustion into one real reminder failure.
- Apply one contract to every supported CurrentSession channel binding.

**Non-Goals:**

- Change ordinary user-message buffer behavior.
- Change Channel or None reminder execution.
- Add a new Akka.Reminders API.
- Change reminder health-count retention or decay.
- Change reminder trust or approval policy.

## Decisions

### Channel bindings own the admission check

Slack, Discord, and Mattermost bindings already track `_turnInFlight`. They will reject a trusted reminder before observer registration or queue admission.

SignalR will track the same state because it has no equivalent field. Each binding will set the state after queue admission and clear it after turn completion or pipeline reset.

This boundary prevents metadata loss and observer leaks. A session-actor check would occur after the binding registers its delivery observer.

### A typed response distinguishes deferral from rejection

`CommandDeferred(SessionId, Reason)` will extend `ISessionResponse`. `CommandNack` will continue to identify permanent rejection.

The execution actor will map `CommandDeferred` to an internal `ReminderExecutionDeferred`. It will not create a failed history record.

### The manager maps deferral to durable negative acknowledgement

The manager will call `NackAsync` with the original envelope and the deferral reason. `RetryScheduled` will release the active execution without Netclaw failure state.

`Failed` or `Expired` means the scheduler cannot retry. The manager will then record one failed history entry and apply the existing terminal failure policy.

This choice consumes Akka delivery attempts. The existing policy provides ten attempts with backoff from one minute to a ten-minute cap.

### Supported gateway absence is transient

The execution actor will distinguish an unsupported origin from a supported gateway that has not registered. The supported case will use the deferral path.

The next Akka attempt will resolve the registry again. Netclaw will not add a local poll loop or retain a second retry queue.

### Accepted turns keep the delivery contract

After admission, `CommandAck`, `ReminderDeliveryResult`, and the one-hour observation timeout keep their current meaning. Transport delivery failures remain execution failures.

## Risks / Trade-offs

- **A session can stay busy through all ten attempts.** The final result becomes one terminal failure.
- **A queue-write timeout can race with successful admission.** Stable reminder identity lets the later Akka attempt use session deduplication.
- **A binding-local busy flag can become stale after a stream fault.** Every pipeline reset clears the flag and fails registered observers.
- **The terminal deferral result arrives after Akka settles the occurrence.** The manager will report any later local persistence failure as a settlement fault.

## Migration Plan

1. Deploy the additive transient response and manager behavior together.
2. Keep the existing reminder JSON and Akka.Reminders database formats.
3. Roll back by restoring the prior binary. No data migration is necessary.

## Open Questions

None.
