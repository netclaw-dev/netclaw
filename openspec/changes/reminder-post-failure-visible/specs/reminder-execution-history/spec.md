## ADDED Requirements

### Requirement: A current-session row records admission

For a `current_session` reminder, the history row SHALL record the admission result. The row says `success = true` when the target session acknowledged the fire. The row says `success = false` when the session or the gateway refused the fire (`CommandNack`) or the acknowledgement timed out.

A post failure after admission SHALL NOT change the row. The session SHALL NOT append a second row. The session reports that failure by the `reminder.delivery.failed` alert and an Error log (see `netclaw-session`, "A failed post on a reminder turn is visible to the operator"). The reminder manager is the only writer of the history.

#### Scenario: A rejected post leaves the admission row unchanged

- **GIVEN** a Mattermost `current_session` reminder, and the session acknowledged the fire
- **WHEN** the Mattermost post API later rejects the reply because the bot token is bad
- **THEN** the history holds one row with `success = true`
- **AND** the operator sees a `reminder.delivery.failed` alert for the reminder

#### Scenario: A refused fire records a failed row

- **GIVEN** a `current_session` reminder fires
- **WHEN** the gateway replies `CommandNack`
- **THEN** the history holds one row with `success = false` and a non-null `errorMessage`
- **AND** the session raises no `reminder.delivery.failed` alert, because no turn ran
