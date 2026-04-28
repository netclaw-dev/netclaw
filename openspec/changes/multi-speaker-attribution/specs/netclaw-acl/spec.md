## MODIFIED Requirements

### Requirement: Channel and sender allow checks

The system SHALL evaluate channel and sender policy before executable turn
dispatch. For threaded channel adapters, only authorized senders SHALL create
live executable inbound turns.

Unauthorized live messages in a thread SHALL NOT be forwarded as ordinary
`SendUserMessage` turns. They remain source-thread material that MAY later be
adopted as quoted context by a subsequent authorized turn, but they do not
independently pass the executable-turn ACL gate.

When a later authorized turn adopts pending thread material, any
`authority-at-inclusion` value recorded for those adopted messages SHALL be
derived from the same live turn-creation authorization basis that the adapter
applies to threaded inbound messages at adoption time.

#### Scenario: Sender allowed, channel allowed

- **GIVEN** sender and channel are explicitly allowed
- **WHEN** a threaded message arrives
- **THEN** ACL evaluation returns allow for executable turn creation

#### Scenario: Sender disallowed

- **WHEN** sender is not allowed by policy
- **THEN** ACL evaluation returns deny
- **AND** no executable turn is dispatched for that live message

#### Scenario: Unauthorized thread message remains non-executable

- **GIVEN** `AllowedUserIds` contains `"U111"`
- **WHEN** user `U999` sends a message in the same thread
- **THEN** the live message is denied for executable turn creation
- **AND** the message does not become a `SendUserMessage`
- **AND** the message may only appear later inside adopted quoted context if an
  authorized user speaks

### Requirement: Mention and ambient mode behavior

The system SHALL respect `require_mention` per channel, and mention or ambient
eligibility SHALL NOT override sender authorization for executable turns.

#### Scenario: Mention-required channel without mention

- **GIVEN** channel has `require_mention=true`
- **WHEN** message has no mention
- **THEN** no model turn is dispatched

#### Scenario: Unauthorized ambient candidate still denied

- **GIVEN** channel has `require_mention=false`
- **AND** sender is not authorized by policy
- **WHEN** the message arrives
- **THEN** the message does not create an executable turn even though the
  channel is ambient-enabled
