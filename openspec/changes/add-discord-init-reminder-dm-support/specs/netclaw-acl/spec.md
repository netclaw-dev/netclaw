## ADDED Requirements

### Requirement: Discord direct-message authorization parity

Discord direct-message traffic SHALL use the same default-deny authorization
posture as Slack: sender and channel checks SHALL run before dispatch, and
denied inputs SHALL not create model turns.

#### Scenario: Discord DM sender denied before dispatch

- **GIVEN** a Discord DM message arrives from a sender not allowed by ACL policy
- **WHEN** inbound ACL evaluation runs
- **THEN** dispatch is denied before session routing
- **AND** the deny decision includes a policy reason for diagnostics

#### Scenario: Discord DM sender and channel allowed

- **GIVEN** ACL policy allows the Discord sender and DM channel context
- **WHEN** inbound ACL evaluation runs
- **THEN** the message is allowed
- **AND** normal session dispatch proceeds

### Requirement: Discord reminder minting enforces audience bounds

Reminder creation from Discord sessions SHALL enforce existing audience
authorization semantics: omitted audience inherits source audience, and
requested audience broader than source authority SHALL be denied before
persistence.

#### Scenario: Omitted audience inherits from Discord DM source

- **GIVEN** a reminder is created from a Discord DM session with source audience `Team`
- **WHEN** the request omits `audience`
- **THEN** the persisted reminder audience is resolved to `Team`

#### Scenario: Broader audience request from Discord DM is denied

- **GIVEN** a reminder is created from a Discord DM session with source audience `Public`
- **WHEN** the request sets `audience` to `Team`
- **THEN** reminder minting is denied before persistence
- **AND** the response explains that requested audience exceeds source authority
