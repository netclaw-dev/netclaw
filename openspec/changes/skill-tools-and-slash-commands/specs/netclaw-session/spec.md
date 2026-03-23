# netclaw-session Delta Spec

## MODIFIED Requirements

### Requirement: Slash-command interception before LLM dispatch

The session actor SHALL intercept user messages starting with `/` and check
the slash-command registry before passing the message to the LLM. This
interception SHALL apply to all message sources (Slack, webhook, scheduled
jobs, reminders).

#### Scenario: Slash command intercepted before LLM

- **GIVEN** a user message starting with `/netclaw-operations`
- **WHEN** the session actor receives the message
- **THEN** the slash-command registry is checked BEFORE any LLM call
- **AND** if matched, the skill content is injected as a transient system message
- **AND** the remainder text becomes the user message for the LLM turn
