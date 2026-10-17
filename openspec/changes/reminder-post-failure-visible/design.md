## Context

See `proposal.md`. The binding of a channel reports a failed post as `DeliveryFailed` to the session. `HandleDeliveryFailedWhenReady` handles it. Today it ignores stale feedback. It retries three failure kinds (`ContentRejected`, `MessageTooLarge`, `UnsupportedContent`). For every other kind it adds a nudge for the next turn. Mattermost and Discord report every post exception as `TransportFailure`, so these two channels never retry.

Schematic flow (omits the security gates):

```
Binding: post fails -> DeliveryFailed{TurnNumber, ChannelType, FailureKind}
Session.HandleDeliveryFailedWhenReady
  turn = recorded turn named by TurnNumber
  reminderKey = turn.SourceReminderId          // durable, in TurnRecorded
  retryable and budget left   -> retry         // no alert yet
  otherwise (final failure):
      nudge or exhaust as today
      if reminderKey parses -> Error log + alert reminder.delivery.failed
```

## Goals / Non-Goals

**Goals:**

- A reply that nobody received, on a turn that a reminder started, shows as an Error log and an alert.
- The data comes from the journal, so it survives a restart.

**Non-Goals:**

- A second history row. The manager owns the history. After one-shot auto-delete, the one-shot can already have a tombstone.
- A retry of a `TransportFailure`. A bad token does not recover on retry.
- Classify Mattermost 401 and 403 as `PermissionDenied`. This is a separate change.
- An alert for an ordinary turn. A person in the thread sees the missing reply.

## Decisions

**Owner: `LlmSessionActor`.** It owns the turn, it receives `DeliveryFailed`, and it already holds the journal data. Alternative: the channel binding. Rejected: it has no link to the turn source, and it would need the same code in three bindings.

**Data: `TurnRecorded.SourceReminderId`.** It holds the fire key `{reminderId}:{fireTimeMs}`. The session reads it for the turn that `DeliveryFailed.TurnNumber` names. It is durable, so a recovered session finds it. Alternative: an in-memory flag set at admission. Rejected: a restart loses it.

**Fire key to reminder id.** The alert uses the reminder id (the slug) as `Source`, as the other reminder alerts do. `ReminderFireKey.TryParse` gives the id. A source key that does not parse still raises the alert. Its `Source` is the whole key text. The session never hides the failure.

**Final failure only.** A retryable failure with budget left triggers a retry, not an alert. When the retry reply posts, no failure exists. The alert fires when the session gives up: a non-retryable kind, or the retry budget is exhausted.

**Required sink.** `LlmSessionActor` takes `IOperationalNotificationSink` as a required dependency. The daemon always registers one. A null sink would turn the alert off without a signal.

**New alert type.** `AlertType.ReminderDeliveryFailed`, at the end of the enum so earlier ordinals stay stable. Wire type `reminder.delivery.failed`. Severity `Warning`. `Source` is the reminder id. Context keys: `reminderId`, `sessionId`, `channel`, `failureKind`, `error`.

## Risks / Trade-offs

- [The session ignores `DeliveryFailed` for a turn that is not the latest eligible turn] -> This is the current stale rule. A recovered session must still treat the latest recorded turn as eligible for the alert. The tasks list this case.
- [An alert for every fire of a recurring reminder with a bad token] -> The webhook sink deduplicates by `Type` and `Source`. The Error log appears for each fire.
- [A retry turn may not carry the reminder source] -> The tasks require that the final failure of a retried reminder turn still finds the reminder.

## Migration Plan

No journal, snapshot, or config change. Older journals hold `SourceReminderId` already.
