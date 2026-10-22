## ADDED Requirements

### Requirement: An open child prompt replays to a new subscriber

When a subscriber joins a session, the session SHALL send it each open parked child prompt again, with the same call
ID, command, scope, and options, with no child identity, and marked as a replay. Each channel binding, the TUI, and
headless output SHALL show a prompt with a given call ID at most once. A channel binding SHALL NOT post a second prompt
for a call ID that it tracks already. When a binding cannot post a replayed prompt, it SHALL log a warning and SHALL
NOT report the prompt as unavailable, so the request stays open.

#### Scenario: A prompt that was never shown appears after recovery

- **GIVEN** the session recorded a park, and the daemon crashed before the prompt output
- **WHEN** the Slack binding joins the recovered session
- **THEN** the binding posts the prompt once
- **AND** a click resumes the child

#### Scenario: A channel does not post a tracked prompt twice

- **GIVEN** a Slack binding that restored a tracked child prompt from its journal
- **WHEN** the session replays the same prompt to the binding
- **THEN** the binding posts no second prompt

#### Scenario: A replayed prompt that fails to post stays open

- **GIVEN** a parked child prompt that the session replays to a binding
- **WHEN** the binding cannot post it
- **THEN** the binding logs a warning
- **AND** the session does not withdraw the request

#### Scenario: A new TUI gets the open child prompt

- **GIVEN** a parked child prompt, and the TUI starts again after a daemon restart
- **WHEN** the TUI joins the session
- **THEN** the TUI shows the prompt once
- **AND** one key answers it and the child resumes
