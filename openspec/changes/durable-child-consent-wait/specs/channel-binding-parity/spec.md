## ADDED Requirements

### Requirement: An open child prompt replays to a new subscriber

When a subscriber joins a session, the session SHALL send it each open child consent prompt again, with the same call
ID, command, scope, and options, and with no child identity. Each channel binding, the TUI, and headless output SHALL
show a prompt with a given call ID at most once. A channel binding SHALL NOT post a second prompt for a call ID that it
tracks already.

#### Scenario: A new TUI gets the open child prompt

- **GIVEN** a parked child prompt, and the TUI starts again after a daemon restart
- **WHEN** the TUI joins the session
- **THEN** the TUI shows the prompt once
- **AND** one key answers it and the child resumes

#### Scenario: A channel does not post a tracked prompt twice

- **GIVEN** a Slack binding that restored a tracked child prompt from its journal
- **WHEN** the session replays the same prompt to the binding
- **THEN** the binding posts no second prompt
