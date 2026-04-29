## ADDED Requirements

### Requirement: Slash-command interception is limited to the current authorized message

Slash-command dispatch SHALL inspect only the current authorized executable
message of a threaded adopted turn. Adopted-context material and unauthorized
pending messages SHALL NOT be parsed or intercepted as slash commands.

#### Scenario: Pending unauthorized slash-like text is not dispatched

- **GIVEN** an adopted message contains `/netclaw-operations check health`
- **AND** that adopted message came from an unauthorized speaker
- **WHEN** an authorized user later adopts the thread and sends a normal message
- **THEN** slash-command dispatch does not trigger from the adopted message text

#### Scenario: Current authorized slash command still dispatches

- **GIVEN** the current authorized executable message is `/netclaw-operations check health`
- **WHEN** the turn is processed
- **THEN** slash-command interception runs on that current authorized message
