## ADDED Requirements

### Requirement: A failed post on a reminder turn is visible to the operator

The session SHALL report a reply that it could not deliver when a reminder fire started the turn. The session owns this decision. `LlmSessionActor` makes it in `HandleDeliveryFailedWhenReady`.

The session SHALL find the reminder from `TurnRecorded.SourceReminderId` of the recorded turn that `DeliveryFailed.TurnNumber` names. This data is durable. A session that recovered from its journal after a restart SHALL find it too. The session SHALL NOT use an in-memory flag for this decision.

The session gives up on a reply when the failure kind is not retryable, or when the retry budget is spent. A retryable failure with budget left SHALL cause a retry and SHALL NOT raise the alert. On the final failure of a turn that a reminder fire started, the session SHALL do all of the following:

- Write one Error log with the reminder id, the session id, the channel, and the failure kind.
- Emit one alert of type `reminder.delivery.failed` with severity `Warning`. The alert `Source` is the reminder id. The alert context holds `reminderId`, `sessionId`, `channel`, `failureKind`, and `error`.
- Write no reminder history row. The reminder manager owns the history.
- Not ask for a new fire of the reminder.

The session SHALL NOT raise `reminder.delivery.failed` for a turn that no reminder fire started. `LlmSessionActor` SHALL receive its `IOperationalNotificationSink` as a required dependency.

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

#### Scenario: A recovered session still finds the reminder

- **GIVEN** the session recorded a reminder turn
- **AND** the session stopped and recovered from its journal
- **WHEN** the binding sends `DeliveryFailed` with `FailureKind = TransportFailure` for that turn
- **THEN** the sink receives one `reminder.delivery.failed` alert with the same reminder id
