## ADDED Requirements

### Requirement: A failed post on a reminder turn is visible to the operator

The session SHALL report a reply that it could not deliver when a reminder fire started the turn. `LlmSessionActor` owns this decision. It makes the decision in one method, `ReportReminderPostFailure`. The `Ready` handler (`HandleDeliveryFailedWhenReady`) and the `Processing` handler of `DeliveryFailed` both call that method. No other code raises `reminder.delivery.failed`.

The session SHALL find the reminder from `TurnRecorded.SourceReminderId` of the recorded turn that `DeliveryFailed.TurnNumber` names. The session keeps this link in `DeliveryRetryHandler`, for the latest recorded turn only. This state is actor-local. The session rebuilds it from three sources:

- A live turn. This includes a turn that a routed skill runs in a sub-agent.
- The journal. Replay of each `TurnRecorded` event gives the same result as the live turn, for every kind of turn.
- The snapshot. `SessionSnapshot.EligibleDeliveryReminderId` holds the fire key of the eligible turn while that turn has no report. `SessionSnapshot.EligibleDeliveryTurnRoutedSkill` holds the routed mark of the eligible turn (see below). A session that restores from a snapshot does not replay the `TurnRecorded` event, so the snapshot must carry both.

The session SHALL record a turn as eligible and as the latest recorded turn before it saves a snapshot for that turn. Thus a snapshot that the reminder turn itself triggers keeps both facts. A passivation snapshot keeps them too.

A routed-skill turn is a turn where a sub-agent runs a skill (`HandleRoutedSkillExecutionCompleted`). The session SHALL record it as eligible, like any other turn, so a failed post on it can raise the alert. The session SHALL mark it as routed, and it SHALL NOT retry a routed-skill turn on the main model. The mark travels as follows:

- `TurnRecorded.RoutedSkill` is a new `bool` on the journal event (proto3 `bool routed_skill = 8`). The session writes `true` for a routed-skill turn. A journal entry written before this field has no value. It SHALL read as `false`, so an old entry is an ordinary turn. A turn that is not routed writes the same bytes as before.
- `DeliveryRetryHandler` keeps the mark with the latest recorded turn. `RecordTurn` takes it, and `IsRoutedSkill(turn)` returns it for the latest recorded turn only.
- `SessionSnapshot.EligibleDeliveryTurnRoutedSkill` is a new `bool` (proto3 `bool eligible_delivery_turn_routed_skill = 13`). It reads `false` when absent.

When a report names a routed-skill turn, the session SHALL treat the first report as the final failure, even when the failure kind is retryable. It SHALL make no model call for it. It SHALL write a Warning, call `ReportReminderPostFailure`, and mark the turn not eligible. No retry budget applies to a routed turn. A routed-skill turn that a person started raises no alert and makes no retry.

The session SHALL accept feedback only for the eligible turn. A binding SHALL send the number of the turn whose output failed to post (see `channel-binding-parity`, "A failed post reports the turn that it belongs to").

The session gives up on a reply when the failure kind is not retryable, or when the retry budget is spent. A retryable failure with budget left SHALL cause a retry and SHALL NOT raise the alert. On the final failure of a turn that a reminder fire started, the session SHALL do all of the following:

- Write one Error log with the reminder id, the session id, the channel, and the failure kind.
- Emit one alert of type `reminder.delivery.failed` with severity `Warning`. The alert `Source` is the reminder id. The alert context holds `reminderId`, `sessionId`, `channel`, `failureKind`, and `error`.
- Write no reminder history row. The reminder manager owns the history.
- Not ask for a new fire of the reminder.

The session SHALL report each recorded turn at most once. One reply can fail more than once, for example a text chunk and then a file upload. The first final failure raises the alert. A later report for the same turn raises nothing, in `Ready` and in `Processing`. A new recorded turn, such as a retry turn, starts with no report. A snapshot SHALL NOT hand the key of a reported turn back to a later incarnation of the session.

The session SHALL NOT drop a report in silence. If the report names a turn that is not the latest recorded turn, the session cannot tell whether a reminder started that turn. It SHALL write one Warning that contains `cannot tell whether a reminder started turn {Turn}`. It SHALL NOT raise the alert. An old snapshot (written before `EligibleDeliveryReminderId`) causes this Warning. The session SHALL NOT invent a reminder for it. If the latest recorded turn has no reminder, the session returns quietly, because a person in the thread sees the missing reply.

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

#### Scenario: A snapshot from before the reminder field loads

- **GIVEN** a stored `SessionSnapshotProto` that has no `eligible_delivery_reminder_id` field
- **WHEN** the session restores from it
- **THEN** the snapshot loads, `EligibleDeliveryTurnNumber` keeps its value, and `EligibleDeliveryReminderId` is null

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
