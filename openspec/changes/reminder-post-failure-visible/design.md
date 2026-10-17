## Context

See `proposal.md`. The binding of a channel reports a failed post as `DeliveryFailed` to the session. Two session handlers receive it. `HandleDeliveryFailedWhenReady` ignores stale feedback. It retries three failure kinds (`ContentRejected`, `MessageTooLarge`, `UnsupportedContent`). For every other kind it adds a nudge for the next turn. The `Processing` handler has no turn check and never retries. Mattermost and Discord report every post exception as `TransportFailure`, so these two channels never retry.

Schematic flow (omits the security gates):

```
Binding: output of turn N fails to post
  hold first failure (kind, message)                  // actor-local, one turn
  TurnCompleted{N} arrives, and no output of N posted
  -> DeliveryFailed{TurnNumber = N, ChannelType, FailureKind}

Session (Ready or Processing)
  Ready only: eligible turn != N -> Warning "stale", return
  retryable and budget left (Ready only) -> retry      // no alert yet
  otherwise (final failure):
      nudge or exhaust as today
      ReportReminderPostFailure(N):
        ReminderOf(N) = UnknownTurn -> Warning "cannot tell whether a reminder started turn N"
        ReminderOf(N) = NoReminder  -> return quietly
        ReminderOf(N) = Reminder    -> TryMarkReported(N) ? Error log + alert : return quietly
```

## Goals / Non-Goals

**Goals:**

- A reply that nobody received, on a turn that a reminder started, shows as an Error log and an alert, on Slack, Discord, and Mattermost.
- One reply raises one alert.
- The link from a turn to its reminder survives a restart, a snapshot restore, and a passivation.
- The session never drops a report in silence.

**Non-Goals:**

- A second history row. The manager owns the history. After one-shot auto-delete, the one-shot can already have a tombstone.
- A retry of a `TransportFailure`. A bad token does not recover on retry.
- Classify Mattermost 401 and 403 as `PermissionDenied`. This is a separate change.
- An alert for an ordinary turn. A person in the thread sees the missing reply.
- An alert for a turn where some outputs posted and some failed (see "Partial delivery").

## Decisions

**Owner: `LlmSessionActor`.** It owns the turn, it receives `DeliveryFailed`, and it already holds the journal data. Alternative: the channel binding. Rejected: it has no link to the turn source, and it would need the same code in three bindings.

**One reporting method.** `ReportReminderPostFailure` is the only code that raises the alert. The `Ready` handler and the `Processing` handler both call it. The `Processing` handler stays. A late report that arrives while the session works on another input is the same failure, so it must not vanish.

**Turn number: the turn whose output failed.** A binding posts text while the turn runs, before it knows the turn number. The engine sets `LastCompletedTurnNumber` only at `TurnCompleted`. So an inline report during turn N carried N-1, and the session ignored it as stale. The fix: the binding holds the first failure of the turn and sends the report from the turn-completion hook with `completed.TurnNumber`, as Slack does. Alternative: track a turn in flight (`LastCompletedTurnNumber + 1`). Rejected: a failed turn or a retry turn can skip a number, and the engine would guess. `TurnCompleted` carries the number from the session, so nothing is guessed. The same rule covers a failed empty-turn fallback post.

**Partial delivery.** The hook runs only when no output of the turn posted. A turn with one posted output and one failed output sends no report. Slack has this rule today. A retry would post the delivered part twice. Discord and Mattermost sent a report for each failed post before this change. They now follow the Slack rule. The cost: a reminder reply that lost one chunk raises no alert. Accept this for now. A later change can add a "partial" failure kind.

**Data: `TurnRecorded.SourceReminderId`.** It holds the fire key `{reminderId}:{fireTimeMs}`. The session reads it for the turn that `DeliveryFailed.TurnNumber` names. `DeliveryRetryHandler` keeps it for the latest recorded turn. Three paths fill it: the live turn, the replay of `TurnRecorded`, and the snapshot restore. One helper, `RecordEligibleTurn`, does `MarkEligible` and `RecordTurn` for the live text turn, the live routed-skill turn, and the replay. One helper stops the paths from drifting apart. Before this change the routed-skill path did neither, so replay and live differed.

**Routed-skill turns: alert, no retry.** A routed-skill turn is a turn where a sub-agent runs a skill. Before this change the live session recorded it with no delivery state, but replay recorded it as eligible. The owner decided: a failed post on a routed-skill reminder turn raises the alert, and a retryable failure does not run the turn again on the main model, because the main model did not produce that reply. Live and recovered sessions must agree. `TurnRecorded` has no field that tells replay about the sub-agent, so the change adds one: `TurnRecorded.RoutedSkill` (proto3 `bool routed_skill = 8`). The live routed path writes `true`. A journal entry without the field reads as `false`, which is an ordinary turn, so no old journal needs a migration. A turn that is not routed writes the same bytes as before. `RecordEligibleTurn` takes the mark and passes it to `DeliveryRetryHandler.RecordTurn(turn, reminder, routedSkill)`. In `HandleDeliveryFailedWhenReady`, a routed turn skips the retry branch: the first report is the final failure. Alternative: skip the record in replay for routed turns. Rejected: replay cannot tell which turns are routed without the same new field, and the alert would still be lost. The snapshot also carries the mark (`EligibleDeliveryTurnRoutedSkill`, field 13), because a restored session does not replay `TurnRecorded`.

**Snapshot order and field.** `MaybeSnapshot()` ran before `MarkEligible`. A snapshot that the reminder turn triggered stored no eligible turn. The order moves: record first, snapshot second. This alone is not enough. A restore from a snapshot skips the `TurnRecorded` event, so `RecordTurn` never runs and the reminder is lost. A passivation snapshot has the same gap. So `SessionSnapshot` gets `EligibleDeliveryReminderId` (proto field 12, `optional string`, null when absent). `BuildSnapshot` writes it only while the eligible turn is recorded, has a reminder, and has no report. `SnapshotOffer` calls `RecordTurn(eligibleTurn, key)` when the field has a value. When the field is null, the session records nothing, so a report finds `UnknownTurn` and the session writes the Warning. This covers an old snapshot and a reported turn after passivation. Alternative: keep the limit and document it. Rejected: the failure is silent, and a supervisor restart is the realistic case.

**Report once.** After the first final failure, `TryMarkReported(N)` returns false for turn N. `ReminderOf` stays non-consuming, so a tri-state answer (`UnknownTurn`, `NoReminder`, `Reminder`) stays accurate. `RecordTurn` with a new turn number clears the mark. A retry turn has a new number, so its final failure gets its own alert. The mark is actor-local, and the snapshot field carries only unreported keys. Thus a later incarnation cannot alert for the same reply.

**Unknown turn.** `ReminderOf` returns `UnknownTurn` when the named turn is not the latest recorded turn. The session writes a Warning and raises nothing. It cannot tell whether a reminder started that turn. A quiet return would hide a lost link.

**Fire key to reminder id.** The alert uses the reminder id (the slug) as `Source`, as the other reminder alerts do. `ReminderFireKey.TryParse` gives the id. A source key that does not parse still raises the alert. Its `Source` is the whole key text, and the session writes a Warning that names the key. The session never hides the failure.

**Final failure only.** A retryable failure with budget left triggers a retry, not an alert. When the retry reply posts, no failure exists. The alert fires when the session gives up: a non-retryable kind, or the retry budget is exhausted.

**Required sink.** `LlmSessionActor` takes `IOperationalNotificationSink` as a required dependency. The daemon always registers one. A null sink would turn the alert off without a signal. The daemon registers a webhook sink only when `Notifications.Webhooks` has an entry. Otherwise it registers `NullNotificationSink`, and only the Error log shows the failure.

**New alert type.** `AlertType.ReminderDeliveryFailed`, at the end of the enum so earlier ordinals stay stable. Wire type `reminder.delivery.failed`. Severity `Warning`. `Source` is the reminder id. Context keys: `reminderId`, `sessionId`, `channel`, `failureKind`, `error`.

## Ownership and lifetime of the data

| Data | Owner | Lifetime |
|------|-------|----------|
| First failed post of a turn (kind, message) | Binding actor | Actor-local, one turn |
| Latest recorded turn, its reminder key, its reported mark | `DeliveryRetryHandler` in the session | Actor-local, rebuilt on recovery |
| `TurnRecorded.SourceReminderId`, `TurnRecorded.RoutedSkill` | Session journal | Durable |
| `SessionSnapshot.EligibleDeliveryReminderId`, `SessionSnapshot.EligibleDeliveryTurnRoutedSkill` | Session snapshot | Durable; unreported keys only |

## Risks / Trade-offs

- [The session ignores `DeliveryFailed` for a turn that is not the latest eligible turn] -> This is the current stale rule. The binding now sends the right number, so the rule holds. The contract tests in `SessionBindingContractTests` prove the number for each channel.
- [An alert for every fire of a recurring reminder with a bad token] -> The webhook sink deduplicates by `Type` and `Source`. The Error log appears for each fire.
- [A retry turn may not carry the reminder source] -> The tests require that the final failure of a retried reminder turn still finds the reminder.
- [A recovered session gets a fresh retry budget for the latest turn] -> `Recover<TurnRecorded>` calls `MarkEligible` with no active chain, so the budget is two retries again. A retry needs a new failure report, and a replayed turn does not resend a delivered reply. Before this change a recovered session had no retry at all. The risk is low.
- [A partial failure sends no report] -> See "Partial delivery".
- [A routed-skill reminder turn cannot retry] -> The session alerts at the first report instead. The operator sees the failure once.

## Migration Plan

Three new proto fields, all with safe defaults: `SessionSnapshotProto.eligible_delivery_reminder_id` (12), `SessionSnapshotProto.eligible_delivery_turn_routed_skill` (13), and `TurnRecordedProto.routed_skill` (8). An older snapshot or journal entry loads with no value, which reads as no reminder and not routed. Older journals hold `SourceReminderId` already. A rollback leaves the field in stored snapshots, and the older code ignores it, because proto3 skips unknown fields.
