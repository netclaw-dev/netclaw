## ADDED Requirements

### Requirement: A failed post on a reminder turn is visible to the operator

The session SHALL report a reply that it could not deliver when a reminder fire started the turn. `LlmSessionActor` owns this decision. It makes the decision in one method, `ReportReminderPostFailure`. Every phase that receives `DeliveryFailed` calls that method (see "Every phase handles a report" below). No other code raises `reminder.delivery.failed`.

The session SHALL find the reminder from `TurnRecorded.SourceReminderId` of the recorded turn that `DeliveryFailed.TurnNumber` names. The session keeps this link in `DeliveryRetryHandler`, for the latest recorded turn only. This state is actor-local. The session rebuilds it from three sources:

- A live turn. This includes a turn that a routed skill runs in a sub-agent.
- The journal. Replay of each `TurnRecorded` event gives the same result as the live turn, for every kind of turn.
- The snapshot. `SessionSnapshot.EligibleDeliveryReminderId` holds the fire key of the eligible turn while that turn has no report. `SessionSnapshot.EligibleDeliveryTurnRoutedSkill` holds the routed mark of the eligible turn (see below). A session that restores from a snapshot does not replay the `TurnRecorded` event, so the snapshot must carry both.

Every snapshot that the session builds SHALL carry a value for the routed mark, `true` or `false`. A snapshot that has a value is a new-format snapshot. A snapshot that has no value was written before the delivery fields (an old-format snapshot). When the session restores a new-format snapshot that names an eligible turn, it SHALL record that turn with the reminder key (null for a person's turn or for a reported turn) and the routed mark. When the session restores an old-format snapshot, it records nothing.

The session SHALL record a turn as eligible and as the latest recorded turn before it saves a snapshot for that turn. Thus a snapshot that the reminder turn itself triggers keeps both facts. A passivation snapshot keeps them too.

A routed-skill turn is a turn where a sub-agent runs a skill (`HandleRoutedSkillExecutionCompleted`). A routed skill runs as a child run, so this turn posts the acceptance of the child. The child-delivery turn of that child posts the child result, and it also counts as a routed-skill turn (see "A child-delivery turn keeps the reminder link of the turn that started the child"). The session SHALL record a routed-skill turn as eligible, like any other turn, so a failed post on it can raise the alert. The session SHALL mark it as routed, and it SHALL NOT retry a routed-skill turn on the main model. The mark travels as follows:

- `TurnRecorded.RoutedSkill` is a new `bool` on the journal event (proto3 `bool routed_skill = 10`). The session writes `true` for a routed-skill turn and for the child-delivery turn of a routed-skill child. A journal entry written before this field has no value. It SHALL read as `false`, so an old entry is an ordinary turn. A turn that is not routed writes the same bytes as before.
- `DeliveryRetryHandler` keeps the mark with the latest recorded turn. `RecordTurn` takes it, and `IsRoutedSkill(turn)` returns it for the latest recorded turn only.
- `SessionSnapshot.EligibleDeliveryTurnRoutedSkill` is a new `bool?` (`optional bool eligible_delivery_turn_routed_skill = 21`). It is `null` when the snapshot has no value. The explicit presence lets the session tell a new-format snapshot (`true` or `false`) from an old-format snapshot (`null`).
- `SessionSnapshot.EligibleDeliveryReminderId` is `optional string eligible_delivery_reminder_id = 20`.

The proto field numbers are durable data. Other open work uses the lower numbers: the coordination stack (#2464) uses `TurnRecordedProto` 8 and `SessionSnapshotProto` 13 to 19 and reserves 12, and #1897 uses `TurnRecordedProto` 8 and 9 and `SessionSnapshotProto` 12. The numbers above are free on `dev` and on every open branch that edits these messages. Another change SHALL NOT reuse 8 or 9 of `TurnRecordedProto`, or 12 to 19 of `SessionSnapshotProto`, for a delivery field. A serialization test SHALL decode a hand-built wire message with each number.

When a report names a routed-skill turn, the session SHALL treat the first report as the final failure, even when the failure kind is retryable. It SHALL make no model call for it. It SHALL write a Warning, call `ReportReminderPostFailure`, and mark the turn not eligible. No retry budget applies to a routed turn. A routed-skill turn that a person started raises no alert and makes no retry.

Every phase handles a report. The session SHALL handle `DeliveryFailed` in `Ready`, `Processing`, `Compacting`, and `Passivating`, and it SHALL NOT drop a report in silence in any of them:

| Phase or state | The session SHALL |
|----------------|-------------------|
| `Ready` | Apply the stale check, the routed-skill rule, and the retry rule. On a final failure, call `ReportReminderPostFailure`. |
| `Processing` | Make no retry, because the session works on another input. Add the nudge for a kind that is not retryable. Call `ReportReminderPostFailure`. |
| `Compacting` | Make no retry. Add no nudge, because compaction rebuilds the history from the state that the session saved when compaction started, and the rebuild would discard the nudge. Write one Warning that contains `adds no nudge` for a kind that is not retryable. Call `ReportReminderPostFailure`. The alert and the Error log are the operator signal. |
| `Passivating`, no restart drain pending | Abort the passivation and handle the report as in `Ready`. |
| Any phase while a restart drain is pending | Call `ReportReminderPostFailure`. Add no nudge, make no retry, and do not abort the drain. The drain ends the session soon, and the failed post is the only trace of the lost reply. |

A report that arrives during compaction is the common case after a long reminder turn: the binding reports only after it handles `TurnCompleted`, and the session compacts right after that event. `ReminderOf(N)` stays correct during compaction, because compaction records no turn.

The session SHALL accept feedback only for the eligible turn. A binding SHALL send the number of the turn whose output failed to post (see `channel-binding-parity`, "A failed post reports the turn that it belongs to").

The session gives up on a reply when the failure kind is not retryable, or when the retry budget is spent. A retryable failure with budget left SHALL cause a retry and SHALL NOT raise the alert. On the final failure of a turn that a reminder fire started, the session SHALL do all of the following:

- Write one Error log with the reminder id, the session id, the channel, and the failure kind.
- Emit one alert of type `reminder.delivery.failed` with severity `Warning`. The alert `Source` is the reminder id. The alert context holds `reminderId`, `sessionId`, `channel`, `failureKind`, and `error`.
- Write no reminder history row. The reminder manager owns the history.
- Not ask for a new fire of the reminder.

The session SHALL report each recorded turn at most once. One reply can fail more than once, for example a text chunk and then a file upload. The first final failure raises the alert. A later report for the same turn raises nothing, in `Ready` and in `Processing`. A new recorded turn, such as a retry turn, starts with no report. A snapshot SHALL NOT hand the key of a reported turn back to a later incarnation of the session.

The session SHALL NOT drop a report in silence. If the report names a turn that is not the latest recorded turn, the session cannot tell whether a reminder started that turn. It SHALL write one Warning that contains `cannot tell whether a reminder started turn {Turn}`. It SHALL NOT raise the alert. An old-format snapshot (it has no routed mark) causes this Warning. The session SHALL NOT invent a reminder for it. If the latest recorded turn has no reminder, the session returns quietly, because a person in the thread sees the missing reply. This holds also after a restore from a new-format snapshot: a report for a person's turn, or for a reminder turn that already has a report, SHALL NOT write the Warning.

If the stored source key is not a reminder fire key (`{reminderId}:{fireTimeMs}`), the session SHALL still raise the alert, with the whole key as `Source`, and SHALL write one Warning that contains `not a reminder fire key`.

The session SHALL NOT raise `reminder.delivery.failed` for a turn that no reminder fire started. `LlmSessionActor` SHALL receive its `IOperationalNotificationSink` as a required dependency. The alert reaches the operator only when the operator configured a notification webhook. Without a webhook, only the Error log shows the failure.

#### Scenario: A rejected token on a reminder turn raises the alert

- **GIVEN** the session recorded a turn with `SourceReminderId = "check-pr:1712000000000"`
- **WHEN** the Mattermost binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for that turn
- **THEN** the session writes one Error log that names `check-pr`
- **AND** the sink receives one alert of type `reminder.delivery.failed` with `Source = "check-pr"` and the session id in its context
- **AND** the reminder history gets no row from the session

#### Scenario: A failed post on an ordinary turn raises no reminder alert

- **GIVEN** the session recorded a turn that a person started, so `SourceReminderId` is empty
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for that turn
- **THEN** the sink receives no `reminder.delivery.failed` alert
- **AND** the session keeps its Warning log and its nudge for the next turn

#### Scenario: A retry that succeeds raises no alert

- **GIVEN** the session recorded a reminder turn
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = ContentRejected`
- **AND** the session retries and the new reply posts
- **THEN** the sink receives no `reminder.delivery.failed` alert

#### Scenario: A spent retry budget raises the alert

- **GIVEN** the session recorded a reminder turn
- **WHEN** the binding reports `ContentRejected` for the turn and for each of its two retry turns
- **THEN** the session gives up after the third report
- **AND** the sink receives one `reminder.delivery.failed` alert

#### Scenario: A report for the second turn of a session is not stale

- **GIVEN** a person started turn 1 and a reminder fire started turn 2
- **WHEN** the binding sends `DeliveryFailed` with `TurnNumber = 2` and `FailureKind = TransportFailure`
- **THEN** the sink receives one `reminder.delivery.failed` alert

#### Scenario: Counterexample - a report that names the previous turn raises no alert

- **GIVEN** a person started turn 1 and a reminder fire started turn 2
- **WHEN** the binding sends `DeliveryFailed` with `TurnNumber = 1`
- **THEN** the session ignores it as stale feedback
- **AND** the sink receives no `reminder.delivery.failed` alert

#### Scenario: Two reports for one reminder turn raise one alert

- **GIVEN** the session recorded a reminder turn
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` twice for that turn
- **THEN** the sink receives exactly one `reminder.delivery.failed` alert after the session handled both reports

#### Scenario: A report while the session processes another input raises no second alert

- **GIVEN** the session raised the alert for turn 1, a reminder turn
- **AND** a person's message holds the session in `Processing`
- **WHEN** the binding sends one more `DeliveryFailed` for turn 1
- **THEN** the sink still holds exactly one `reminder.delivery.failed` alert

#### Scenario: A report for an unknown turn writes a Warning

- **GIVEN** the latest recorded turn is turn 1, and the session is in `Processing`
- **WHEN** the binding sends `DeliveryFailed` with `TurnNumber = 41`
- **THEN** the session writes one Warning that contains `cannot tell whether a reminder started turn`
- **AND** the sink receives no `reminder.delivery.failed` alert

#### Scenario: A recovered session still finds the reminder

- **GIVEN** the session recorded a reminder turn
- **AND** the session stopped and recovered from its journal
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for that turn
- **THEN** the sink receives one `reminder.delivery.failed` alert with the same reminder id

#### Scenario: A snapshot that the reminder turn triggers keeps the reminder

- **GIVEN** the snapshot interval is 1, so the reminder turn saves a snapshot
- **AND** the session stopped and recovered from that snapshot, without a replay of the reminder turn
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for that turn
- **THEN** the sink receives one `reminder.delivery.failed` alert with the same reminder id

#### Scenario: A session that passivated still finds the reminder

- **GIVEN** the session recorded a reminder turn, passivated, and saved its final snapshot
- **WHEN** a new session incarnation restores from that snapshot
- **AND** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for that turn
- **THEN** the sink receives one `reminder.delivery.failed` alert with the same reminder id

#### Scenario: A reported turn does not alert again after passivation

- **GIVEN** the session raised the alert for a reminder turn and then passivated
- **WHEN** the new incarnation receives one more `DeliveryFailed` for that turn
- **THEN** the sink still holds exactly one `reminder.delivery.failed` alert
- **AND** the session writes no Warning, because the new-format snapshot names the turn and holds no unreported reminder

#### Scenario: A snapshot from before the delivery fields loads

- **GIVEN** a stored `SessionSnapshotProto` that has no `eligible_delivery_reminder_id` field and no routed mark
- **WHEN** the session restores from it
- **THEN** the snapshot loads, `EligibleDeliveryTurnNumber` keeps its value, `EligibleDeliveryReminderId` is null, and `EligibleDeliveryTurnRoutedSkill` is null

#### Scenario: Counterexample - an old-format snapshot still writes the Warning

- **GIVEN** the session restored from a snapshot that names an eligible turn and has no routed mark
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for that turn
- **THEN** the session writes one Warning that contains `cannot tell whether a reminder started turn`
- **AND** the sink receives no `reminder.delivery.failed` alert

#### Scenario: A person's turn restored from a new-format snapshot returns quietly

- **GIVEN** a person started the eligible turn, the session passivated, and a new incarnation restored from the final snapshot (routed mark `false`, no reminder)
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for that turn
- **THEN** the session writes no Warning and the sink receives no `reminder.delivery.failed` alert

#### Scenario: A report while the session compacts raises the alert

- **GIVEN** a reminder turn crossed the compaction threshold, and the session is in `Compacting`
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for that turn
- **THEN** the session writes one Error log that names the reminder
- **AND** the sink receives one `reminder.delivery.failed` alert before compaction ends
- **AND** the session adds no nudge, so the next model call holds no delivery-failure message

#### Scenario: A report while a restart drain is pending raises the alert

- **GIVEN** a reminder turn is recorded, and a restart drain is pending while the session is in `Processing` or `Passivating`
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for that turn
- **THEN** the sink receives one `reminder.delivery.failed` alert
- **AND** the session adds no nudge, makes no model call, and does not abort the drain

#### Scenario: A routed-skill reminder turn raises the alert

- **GIVEN** a reminder prompt runs a routed skill (`/ops-route ...`) in a sub-agent, and the session recorded the turn with `RoutedSkill = true`
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for that turn
- **THEN** the sink receives one `reminder.delivery.failed` alert
- **AND** a session that replays the journal gives the same result

#### Scenario: A retryable failure on a routed-skill reminder turn alerts and makes no retry

- **GIVEN** the session recorded a routed-skill reminder turn
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = ContentRejected` once or twice for that turn
- **THEN** the sink receives exactly one `reminder.delivery.failed` alert
- **AND** the session makes no model call for the report
- **AND** a session that replayed the journal, or restored from a snapshot, gives the same result

#### Scenario: Counterexample - an ordinary reminder turn still retries after recovery

- **GIVEN** the session recorded a reminder turn with `RoutedSkill = false`, or from a journal entry with no `routed_skill` field
- **AND** the session recovered from its journal
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = ContentRejected` for that turn
- **THEN** the session asks the model again, and it raises no alert when the retry reply posts

#### Scenario: Counterexample - a routed-skill turn that a person started raises no alert and makes no retry

- **GIVEN** a person ran a routed skill, so `SourceReminderId` is empty and `RoutedSkill = true`
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = ContentRejected` for that turn, before or after recovery
- **THEN** the sink receives no `reminder.delivery.failed` alert
- **AND** the session makes no model call for the report

### Requirement: A child-delivery turn keeps the reminder link of the turn that started the child

A routed skill and an async `spawn_agent` call both start a child run (see `background-subagent-runs`). The turn that starts the child posts only the acceptance. A later child-delivery turn gives the child result to the main model and posts the real reply. That turn has no input of its own, so it has no input source with a reminder id.

The session SHALL record a child-delivery turn with the reminder link of the turn that started the child. The rules of "A failed post on a reminder turn is visible to the operator" then apply to it without change. `LlmSessionActor` owns this decision.

Data and owners:

| Data | Owner | Lifetime |
|------|-------|----------|
| `BackgroundChildRun.SourceReminderId` | `LlmSessionActor.AcceptChild`, in the `ChildRunAccepted` event | Durable: `BackgroundChildRunV2Proto.source_reminder_id = 20`, in the journal event and in the snapshot child list |
| `TurnRecorded.SourceReminderId` of the child-delivery turn (`RoutedSkill` stays false) | `LlmSessionActor.HandleTextResponse` | Durable: the existing journal field |
| The link of a pending retry | `DeliveryRetryHandler.RetriedTurnReminder` | Actor-local, from the retry until the next recorded turn |

The field number 20 is the next number after the fields 1 to 19 of `BackgroundChildRunV2Proto` on `dev`. No open branch edits this message. The other open branches that edit the proto file (#1897, #1730) add other messages. The field has explicit presence (`optional string`).

The session SHALL find the link of the running turn in this order (schematic; it omits the security gates):

```
RunningTurnLink(deliveredChild):
  deliveredChild is not null           -> deliveredChild.SourceReminderId
  the turn's own input has a reminder  -> that fire key
  a delivery retry is pending          -> the link of the turn that the retry repeats
  otherwise                            -> null
```

- `AcceptChild` writes the link of the running turn into the new child run. Thus a child that a child-delivery turn starts keeps the same link.
- When the session records a child-delivery turn, it writes the link of the child into `TurnRecorded.SourceReminderId`. The live turn, the journal replay, and the snapshot then carry the link with no other change. The link follows the child that the turn delivers, not the latest recorded turn.
- A retry turn has no input. It SHALL keep the link of the turn that it repeats. `DeliveryRetryHandler.RecordRetry` starts the pending link. The next `RecordTurn` and `Clear` end it.
- A new input calls `Clear`, so it ends the retry chain and the link together. When a new input ends a pending link, the session SHALL write one Warning: `A new input ended the delivery retry of turn {Turn} for reminder {FireKey}`. `{Turn}` is the turn that the retry repeats. `{FireKey}` is the link. The session raises no alert for it, because the person who wrote the input is in the thread. The Warning makes the end of the link visible. `LlmSessionActor` owns this decision.
- A retry turn cannot start a child today. A tool call in a retry turn fails with `Tool batch dispatch requires admitted turn authority`, and that guard is on `dev`. This change does not alter it. #2548 tracks the guard. A test of a child that a retry turn starts must come with the fix for #2548. The child-delivery turn is therefore the only turn with no input that starts a child.

A child-delivery turn SHALL be an ordinary turn for the retry rule, whatever started the child. This holds for a routed-skill child (`ChildRunStartKey.Slash`) and for an async `spawn_agent` child (`ChildRunStartKey.Tool`). The main model writes the reply of a child-delivery turn from the `child_run_result` pair. A retry can fix that reply, for example a shorter reply after `MessageTooLarge`. So the session retries up to the budget, and it alerts once when the budget is spent or the kind is not retryable, if a reminder started the child. `TurnRecorded.RoutedSkill` is false on a child-delivery turn. Only the routed turn that accepts the child follows the rule "alert, no retry", because a sub-agent wrote its reply. Before this requirement, a person-started routed child result retried. That behavior stays.

The `TurnCompleted` output of a child-delivery turn SHALL NOT change. Its `SourceReminderId` stays null. The reminder execution actor reads that output to find the turn of its own fire, and a child-delivery turn is not that turn.

Coordination with #2515 (`feature/coordination-child-notices`): that change removes the child-delivery turn. A child result becomes a notice input. Its `InputAdmitted` event has no `SourceMessageId`, so `RecordedSource` gives no `ReminderId`. The notice turn would record `SourceReminderId = selected.ReminderId`, which is null, and the alert for a child result would be lost again with no error. Whichever of the two changes merges second SHALL make the notice turn read `_state.ChildRuns[notice.RunId].SourceReminderId`. A turn takes the link of the first input that has one: its own `ReminderId`, or the `SourceReminderId` of the child that a notice names. Each notice turn must pass the noticed run as `deliveredChild` to `AcceptChild`, because `SelectedContinuation` does not exist in #2515. The second PR must add a test for a turn that consumes several inputs. The rules of this requirement hold for the turn that posts a child result, whatever the name of that turn. Keep these tests: every test in `ChildDeliveryReminderLinkTests`, every test in `ChildDeliveryReminderLinkSnapshotTests`, and the child-result tests in `RoutedSkillReminderDeliveryTests`. This list includes the counterexamples `A_failed_post_of_the_child_result_of_a_person_turn_raises_no_alert` and `A_new_input_during_the_retry_of_a_person_turn_writes_no_warning`. The tests check behavior, so the second PR may rename them or change their arrangement, for example "the next turn" for "the child-delivery turn". The recovery path of a notice turn differs in #2515, so the snapshot and replay tests may change their arrangement. Each scenario stays. Do not delete them as tests of a pair projection. The routed mark follows the rule above: a notice turn writes `RoutedSkill = false`.

Old data: a child run record from before field 20 SHALL read as no link. Its child-delivery turn raises no alert and writes no Warning. The session cannot tell a child that a person started from a child that an older reminder turn started. A draft v1 child record and a deployed v1 child record also read as no link.

The `netclaw-operations` skill (`references/scheduling.md`) SHALL say that the alert also covers the later reply that posts the result of a child run that a reminder started, for a routed skill and for an async sub-agent. Before this requirement, the skill said that the alert holds "for a reminder that runs a routed skill". That statement was true only for the acceptance post.

#### Scenario: A failed post of the child result of a reminder turn raises the alert

- **GIVEN** a reminder fire `check-pr:1712000000000` started turn 1, and turn 1 called `spawn_agent`
- **AND** the child finished, and the session recorded the child-delivery turn 2
- **WHEN** the binding sends `DeliveryFailed` with `TurnNumber = 2` and `FailureKind = TransportFailure`
- **THEN** the sink receives one `reminder.delivery.failed` alert with `Source = "check-pr"`
- **AND** the `TurnCompleted` output of turn 2 has no `SourceReminderId`

#### Scenario: Counterexample - the child result of a person's turn raises no alert

- **GIVEN** a person started turn 1, and turn 1 called `spawn_agent`
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for the child-delivery turn
- **THEN** the sink receives no `reminder.delivery.failed` alert

#### Scenario: A spent retry budget on the child result of a reminder turn raises the alert

- **GIVEN** the child-delivery turn of a reminder-started `spawn_agent` child
- **WHEN** the binding reports `ContentRejected` for that turn and for each of its two retry turns
- **THEN** the session retries twice on the main model
- **AND** the sink receives one `reminder.delivery.failed` alert after the third report

#### Scenario: The child result of a routed-skill reminder retries and then alerts once

- **GIVEN** a reminder prompt runs a routed skill, and the session recorded the child-delivery turn of that child with `RoutedSkill = false`
- **WHEN** the binding reports `ContentRejected` for that turn and for each of its two retry turns
- **THEN** the session retries twice on the main model, and the sink receives no alert for the first two reports
- **AND** the sink receives exactly one `reminder.delivery.failed` alert after the third report
- **AND** a session that replayed the journal, or restored from a snapshot, gives the same result

#### Scenario: Counterexample - the child result of a routed skill that a person started retries and raises no alert

- **GIVEN** a person ran a routed skill, and the session recorded the child-delivery turn of that child
- **WHEN** the binding reports `ContentRejected` for that turn and for each of its two retry turns
- **THEN** the session retries twice on the main model, as it does for any ordinary turn
- **AND** the sink receives no `reminder.delivery.failed` alert

#### Scenario: A person turn between the reminder start and the child result does not move the link

- **GIVEN** a reminder fire started turn 1, and turn 1 called `spawn_agent`
- **AND** a person wrote turn 2 while the child ran, and the child-delivery turn is turn 3
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for turn 3
- **THEN** the sink receives one `reminder.delivery.failed` alert with `Source = "check-pr"`

#### Scenario: Counterexample - a reminder turn between the person start and the child result does not move the link

- **GIVEN** a person started turn 1, and turn 1 called `spawn_agent`
- **AND** a reminder fire ran as turn 2 while the child ran, and the child-delivery turn is turn 3
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for turn 3
- **THEN** the sink receives no `reminder.delivery.failed` alert

#### Scenario: A child that a child-delivery turn started keeps the link

- **GIVEN** a reminder turn started child 1, and the child-delivery turn of child 1 started child 2
- **WHEN** child 2 finished, and the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for the child-delivery turn of child 2
- **THEN** the sink receives one `reminder.delivery.failed` alert with `Source = "check-pr"`

#### Scenario: One fire raises one alert for each failed reply

- **GIVEN** a reminder turn that called `spawn_agent`, so the acceptance post is turn 1 and the result post is turn 2
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for turn 1 and then for turn 2
- **THEN** the sink receives two `reminder.delivery.failed` alerts with `Source = "check-pr"`, one for each turn
- **AND** a second report for the same turn adds no alert

#### Scenario: A new input during the retry of a child result writes a Warning

- **GIVEN** the child-delivery turn of a reminder-started child, and the binding reported `ContentRejected` for it, so the retry turn waits on the main model
- **WHEN** a person writes a message, and the session buffers it
- **THEN** the session writes one Warning that contains `ended the delivery retry of turn 2 for reminder check-pr:1712000000000`
- **AND** the sink receives no `reminder.delivery.failed` alert

#### Scenario: Counterexample - a person turn after a reminder turn has no link

- **GIVEN** a reminder fire ran as turn 1, and a person wrote turn 2
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for turn 2
- **THEN** the sink receives no `reminder.delivery.failed` alert
- **AND** the result is the same when turn 1 had a delivery retry turn before turn 2

#### Scenario: The link survives a restart while the child runs

- **GIVEN** a reminder turn started a child, and the session stopped while the child ran
- **WHEN** the session recovers from its journal, or from a snapshot that holds the accepted child
- **AND** the session records the child as lost and records the child-delivery turn
- **AND** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for that turn
- **THEN** the sink receives one `reminder.delivery.failed` alert with `Source = "check-pr"`

#### Scenario: The link survives a restart after the child-delivery turn

- **GIVEN** the session recorded the child-delivery turn of a reminder-started child
- **WHEN** the session recovers from its journal, from a snapshot that the turn saved, or from a passivation snapshot
- **AND** the binding sends `DeliveryFailed` for that turn
- **THEN** the session gives the same result as the live session: it retries a retryable kind up to the budget, and it raises one alert for the final failure

#### Scenario: A child run record from before the link loads with no link

- **GIVEN** a stored `BackgroundChildRunV2Proto` with no field 20
- **WHEN** the session reads it from the journal or from a snapshot
- **THEN** the record loads, and `BackgroundChildRun.SourceReminderId` is null
- **AND** a hand-built record with a string at field 20 reads that string as the link
