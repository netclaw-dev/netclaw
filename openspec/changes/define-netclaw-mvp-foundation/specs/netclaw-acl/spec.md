## ADDED Requirements

### Requirement: Channel and sender allow checks

The system SHALL evaluate channel and sender policy before turn dispatch.

#### Scenario: Sender disallowed

- **WHEN** sender is not allowed by policy
- **THEN** ACL evaluation returns deny

### Requirement: Mention and ambient mode behavior

The system SHALL respect `require_mention` per channel.

#### Scenario: Mention-required channel without mention

- **GIVEN** channel has `require_mention=true`
- **WHEN** message has no mention
- **THEN** no model turn is dispatched
