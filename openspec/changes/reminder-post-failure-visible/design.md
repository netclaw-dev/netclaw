## Context

See `proposal.md`. The binding of a channel reports a failed post as `DeliveryFailed` to the session. Two session handlers receive it. `HandleDeliveryFailedWhenReady` ignores stale feedback. It retries three failure kinds (`ContentRejected`, `MessageTooLarge`, `UnsupportedContent`). For every other kind it adds a nudge for the next turn. The `Processing` handler has no turn check and never retries. Mattermost and Discord report every post exception as `TransportFailure`, so these two channels never retry.

Schematic flow (omits the security gates):

```
Binding: output of turn N fails to post
  hold first failure (kind, message)                  // actor-local, one turn
  TurnCompleted{N} arrives, and no output of N posted
  -> DeliveryFailed{TurnNumber = N, ChannelType, FailureKind}

Session (any phase: Ready, Processing, Compacting, Passivating)
  restart drain pending -> ReportReminderPostFailure(N) only; no nudge, no retry
  Ready only: eligible turn != N -> Warning "stale", return
  routed-skill turn, retryable -> no retry; treat as final failure
  retryable and budget left (Ready only) -> retry      // no alert yet
  otherwise (final failure):
      nudge or exhaust as today (Processing: nudge, never retry; Compacting: no nudge, never retry)
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
- The session never drops a report in silence, in any phase.

**Non-Goals:**

- A second history row. The manager owns the history. After one-shot auto-delete, the one-shot can already have a tombstone.
- A retry of a `TransportFailure`. A bad token does not recover on retry.
- Classify Mattermost 401 and 403 as `PermissionDenied`. This is a separate change.
- An alert for an ordinary turn. A person in the thread sees the missing reply.
- An alert for a turn where some outputs posted and some failed (see "Partial delivery").

## Decisions

**Owner: `LlmSessionActor`.** It owns the turn, it receives `DeliveryFailed`, and it already holds the journal data. Alternative: the channel binding. Rejected: it has no link to the turn source, and it would need the same code in three bindings.

**One reporting method.** `ReportReminderPostFailure` is the only code that raises the alert. Every handler of `DeliveryFailed` calls it: `Ready`, `Processing`, `Compacting`, and `Passivating`. A late report that arrives while the session works on another input is the same failure, so it must not vanish.

**Every phase handles a report (review round 3).** The binding reports after it handles `TurnCompleted`. A long reminder turn makes the session compact right after that event, so the report lands in `Compacting`. Before round 3 that phase had no handler, and the report became a dead letter with only an Info line. The restart-drain branches in `Processing` and `Passivating` returned with an Info line too. The decision: `Compacting` copies the `Processing` rule except for the nudge: no retry, no nudge, then the report. The nudge is lost when compaction succeeds, because `Apply(SessionCompacted)` builds the history again from the state that the session saved when compaction started. A kept nudge would need an actor-local field, and the nudge is only a hint to the model. The alert is the operator signal. A pending restart drain reports and returns. It adds no nudge and makes no retry, because the drain ends the session soon and the post has failed already. The alternative, a Warning that names the turn, was rejected: the lost reply is the exact case that the alert exists for, and the report costs one method call. `ReminderOf(N)` stays correct during compaction, because compaction records no turn.

**Held failure: output posts only (review round 3).** `SafeTransportCall` calls one notifier for every failed post, and the Discord and Mattermost notifier was the hold. So a failed warning post (ingress closed, a blocked live message, an attachment or backfill warning, the wrong-requester warning) was held too. It can occur between turns, and `??=` then kept it in place of the real failure of the next turn, so the report and the alert carried the wrong `error` text. The fix: the hold comes only from the output wrappers that the engine calls (`postTextAsync` and `uploadFileAsync`), as Slack does with `PostOutputTextAsync` and `UploadOutputFileAsync`. The notifier of `SafeTransportCall` for other posts only logs. The approval-prompt posts of Discord and Mattermost do not use `SafeTransportCall` today, so they are not held. A test covers the warning post, which is reachable.

**Turn number: the turn whose output failed.** A binding posts text while the turn runs, before it knows the turn number. The engine sets `LastCompletedTurnNumber` only at `TurnCompleted`. So an inline report during turn N carried N-1, and the session ignored it as stale. The fix: the binding holds the first failure of the turn and sends the report from the turn-completion hook with `completed.TurnNumber`, as Slack does. Alternative: track a turn in flight (`LastCompletedTurnNumber + 1`). Rejected: a failed turn or a retry turn can skip a number, and the engine would guess. `TurnCompleted` carries the number from the session, so nothing is guessed. The same rule covers a failed empty-turn fallback post.

**Partial delivery.** The hook runs only when no output of the turn posted. A turn with one posted output and one failed output sends no report. Slack has this rule today. A retry would post the delivered part twice. Discord and Mattermost sent a report for each failed post before this change. They now follow the Slack rule. The cost: a reminder reply that lost one chunk raises no alert. Accept this for now. A later change can add a "partial" failure kind.

**Data: `TurnRecorded.SourceReminderId`.** It holds the fire key `{reminderId}:{fireTimeMs}`. The session reads it for the turn that `DeliveryFailed.TurnNumber` names. `DeliveryRetryHandler` keeps it for the latest recorded turn. Three paths fill it: the live turn, the replay of `TurnRecorded`, and the snapshot restore. One helper, `RecordEligibleTurn`, does `MarkEligible` and `RecordTurn` for the live text turn, the live routed-skill turn, and the replay. One helper stops the paths from drifting apart. Before this change the routed-skill path did neither, so replay and live differed.

**Routed-skill turns: alert, no retry.** A routed-skill turn is a turn where a sub-agent runs a skill. Before this change the live session recorded it with no delivery state, but replay recorded it as eligible. The owner decided: a failed post on a routed-skill reminder turn raises the alert, and a retryable failure does not run the turn again on the main model, because the main model did not produce that reply. Live and recovered sessions must agree. `TurnRecorded` has no field that tells replay about the sub-agent, so the change adds one: `TurnRecorded.RoutedSkill` (proto3 `bool routed_skill = 10`). The live routed path writes `true`. A journal entry without the field reads as `false`, which is an ordinary turn, so no old journal needs a migration. A turn that is not routed writes the same bytes as before. `RecordEligibleTurn` takes the mark and passes it to `DeliveryRetryHandler.RecordTurn(turn, reminder, routedSkill)`. In `HandleDeliveryFailedWhenReady`, a routed turn skips the retry branch: the first report is the final failure. Alternative: skip the record in replay for routed turns. Rejected: replay cannot tell which turns are routed without the same new field, and the alert would still be lost. The snapshot also carries the mark (`EligibleDeliveryTurnRoutedSkill`, `optional bool`, field 21), because a restored session does not replay `TurnRecorded`.

**Child-delivery turns keep the reminder link (rebase onto the coordination stack).** The stack runs a routed skill and an async `spawn_agent` call as child runs. The turn that starts the child posts only the acceptance. A later child-delivery turn posts the real reply, and it has no input, so its `TurnRecorded.SourceReminderId` was null. A probe on the combined tree showed no alert for a failed post of that reply. Decision (option a of the open question): the child-delivery turn keeps the link of the turn that started the child.

- Data source. The child run record is the only durable data that ties a child to its starting turn. It holds the authority record and the start turn id, but not the fire key: the gateway path gives the turn a fresh `TurnId`, and `TurnContextRecord.SourceKind` names "reminder" without the fire. So the record gets one new field, `BackgroundChildRun.SourceReminderId`. `AcceptChild` writes it in the `ChildRunAccepted` event. The proto field is `BackgroundChildRunV2Proto.source_reminder_id = 20` (`optional string`). That message is in the journal event and in the snapshot child list, so one field covers replay and restore. Fields 1 to 19 are in use on `dev`. No open branch edits the message.
- Copy, not lookup. When the session records the child-delivery turn, it copies the link into `TurnRecorded.SourceReminderId` and sets `TurnRecorded.RoutedSkill` for a routed-skill child (`ChildRunStartKey.Slash`). The existing paths then work: `RecordEligibleTurn`, the replay of `TurnRecorded`, and the snapshot fields 20 and 21. Alternative: look up the child at report time. Rejected: the session would need the child record after the turn closes it, and replay would need the same lookup.
- One helper. `RunningTurnReminder(deliveredChild)` gives the link of the running turn: the child link for a child-delivery turn, else the reminder of the turn's own input, else the link of a pending retry. `AcceptChild` and `HandleTextResponse` both call it. Thus a child that a child-delivery turn starts keeps the link too.
- Routed rule. A child-delivery turn of a routed-skill child counts as routed: the first report alerts, and the session makes no model call. A routed skill gives one result in two posts, and both follow one rule. A child of `spawn_agent` is an ordinary turn and keeps the retry budget.
- Retry turn. A retry turn of a child-delivery turn has no input and no child, so it had no link. `DeliveryRetryHandler` keeps the link of the retried turn from `RecordRetry` until the next `RecordTurn` or `Clear`. A retry of an ordinary reminder turn finds the same key through its input source, so it does not change.
- `TurnCompleted` does not change. The reminder execution actor matches that output to its own fire.
- Old data. A child record without field 20 reads as no link. Its child-delivery turn raises no alert and writes no Warning, because the session cannot tell it from a child that a person started.

**Snapshot order and field.** `MaybeSnapshot()` ran before `MarkEligible`. A snapshot that the reminder turn triggered stored no eligible turn. The order moves: record first, snapshot second. This alone is not enough. A restore from a snapshot skips the `TurnRecorded` event, so `RecordTurn` never runs and the reminder is lost. A passivation snapshot has the same gap. So `SessionSnapshot` gets `EligibleDeliveryReminderId` (proto field 20, `optional string`, null when absent). `BuildSnapshot` writes it only while the eligible turn is recorded, has a reminder, and has no report. The routed mark is `optional bool` (field 21), and `BuildSnapshot` writes it for every snapshot, so a value means a new-format snapshot. `SnapshotOffer` calls `RecordTurn(eligibleTurn, key, routed)` for every new-format snapshot with an eligible turn, also when the key is null and the mark is false. A report for such a turn finds `NoReminder` and returns quietly. Before round 3 the session recorded nothing when it had no key and no routed mark, so a late report for a person's turn or for a reported turn wrote a false "cannot tell" Warning. When the snapshot has no routed mark (an old-format snapshot), the session records nothing, so a report finds `UnknownTurn` and the session writes the Warning. Alternative: keep the limit and document it. Rejected: the failure is silent, and a supervisor restart is the realistic case.

**Field numbers (review round 3).** The first numbers (8, 12, 13) collide with the coordination stack (#2464) and #1897. The stack writes `TurnRecordedProto` 8 (`child_continuation`), reserves `SessionSnapshotProto` 12, and writes 13 to 19 there. #1897 writes `TurnRecordedProto` 8 and 9 and `SessionSnapshotProto` 12. A local daemon built from either branch can hold data with those numbers and other wire types. Rule: use the lowest number that is free on `dev` and on every open branch that edits the message. Result: `TurnRecordedProto.routed_skill = 10`, `SessionSnapshotProto.eligible_delivery_reminder_id = 20`, and `SessionSnapshotProto.eligible_delivery_turn_routed_skill = 21`. Both open stacks now keep their numbers. This PR renumbers, because it ships no durable data yet. Tests decode a hand-built wire message with each number, so a later collision fails a test.

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
| `BackgroundChildRun.SourceReminderId` (link of the turn that started the child) | Session journal (`ChildRunAccepted`) and snapshot child list | Durable |
| Link of a pending retry (`DeliveryRetryHandler.RetriedTurnReminder`) | `DeliveryRetryHandler` in the session | Actor-local, from the retry until the next recorded turn |
| `SessionSnapshot.EligibleDeliveryReminderId`, `SessionSnapshot.EligibleDeliveryTurnRoutedSkill` | Session snapshot | Durable; unreported keys only |

## Risks / Trade-offs

- [The session ignores `DeliveryFailed` for a turn that is not the latest eligible turn] -> This is the current stale rule. The binding now sends the right number, so the rule holds. The contract tests in `SessionBindingContractTests` prove the number for each channel.
- [An alert for every fire of a recurring reminder with a bad token] -> The webhook sink deduplicates by `Type` and `Source`. The Error log appears for each fire.
- [A retry turn may not carry the reminder source] -> The tests require that the final failure of a retried reminder turn still finds the reminder.
- [A recovered session gets a fresh retry budget for the latest turn] -> `Recover<TurnRecorded>` calls `MarkEligible` with no active chain, so the budget is two retries again. A retry needs a new failure report, and a replayed turn does not resend a delivered reply. Before this change a recovered session had no retry at all. The risk is low.
- [A partial failure sends no report] -> See "Partial delivery".
- [A routed-skill reminder turn cannot retry] -> The session alerts at the first report instead. The operator sees the failure once.
- [A child started before this change has no link] -> Its child-delivery turn raises no alert. This holds only for a child that was open during the upgrade.
- [A person's message during the retry of a child-delivery turn ends the link] -> `Clear` ends the retry chain and the link. The person is in the thread and sees the missing reply.

## Migration Plan

Four new proto fields, all with safe defaults: `BackgroundChildRunV2Proto.source_reminder_id` (20, explicit presence), `SessionSnapshotProto.eligible_delivery_reminder_id` (20), `SessionSnapshotProto.eligible_delivery_turn_routed_skill` (21, explicit presence), and `TurnRecordedProto.routed_skill` (10). An older snapshot loads with no value for each field. For the routed mark that means "old format", and the session records no turn from it. An older journal entry reads as not routed. An older child run record reads as no link. Older journals hold `TurnRecorded.SourceReminderId` already. A rollback leaves the field in stored snapshots, and the older code ignores it, because proto3 skips unknown fields.
