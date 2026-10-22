## ADDED Requirements

### Requirement: An open child prompt replays to a new subscriber

When a subscriber joins a session, the session SHALL send it each open parked child prompt again, with the same call
ID, command, scope, and options, with no child identity, and marked as a replay. Each channel binding, the TUI, and
headless output SHALL show a prompt with a given call ID at most once. A channel binding SHALL NOT post a second prompt
for a call ID that it tracks already. When a binding cannot render a replayed prompt at all (for example, the channel
cannot show buttons), or the channel API refuses the post with an error code that the binding lists as permanent, the
binding SHALL report the prompt as unavailable, and the session SHALL withdraw the request. When the post of a replayed
prompt fails for any other reason, the binding SHALL log a warning and SHALL NOT report the prompt as unavailable, so
the request stays open for the next join.

#### Scenario: A prompt that was never shown appears after recovery

- **GIVEN** the session recorded a park, and the daemon crashed before the prompt output
- **WHEN** the Slack binding joins the recovered session
- **THEN** the binding posts the prompt once
- **AND** a click resumes the child

#### Scenario: A channel does not post a tracked prompt twice

- **GIVEN** a Slack binding that restored a tracked child prompt from its journal
- **WHEN** the session replays the same prompt to the binding
- **THEN** the binding posts no second prompt

#### Scenario: A replayed prompt with a transport error stays open

- **GIVEN** a parked child prompt that the session replays to a binding
- **WHEN** the post fails with a transport error
- **THEN** the binding logs a warning
- **AND** the session does not withdraw the request

#### Scenario: A replayed prompt with a permanent API error is withdrawn

- **GIVEN** a parked child prompt that the session replays to a Slack binding
- **WHEN** Slack refuses the post with `is_archived`
- **THEN** the binding reports the prompt as unavailable
- **AND** the session withdraws the request

#### Scenario: A replayed prompt that the channel cannot render is withdrawn

- **GIVEN** a parked child prompt that the session replays to a Mattermost binding with no callback URL
- **WHEN** the binding receives the replay
- **THEN** the binding reports the prompt as unavailable
- **AND** the session withdraws the request, and the pause does not hold forever

#### Scenario: A new TUI gets the open child prompt

- **GIVEN** a parked child prompt, and the TUI starts again after a daemon restart
- **WHEN** the TUI joins the session
- **THEN** the TUI shows the prompt once
- **AND** one key answers it and the child resumes
