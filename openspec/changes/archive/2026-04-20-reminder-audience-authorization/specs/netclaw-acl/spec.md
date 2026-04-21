## ADDED Requirements

### Requirement: Reminder audience authorization

The system SHALL authorize reminder minting against the creator's current
source audience / authority before a reminder definition is persisted. A
requested reminder audience SHALL be accepted only when it is equal to or
narrower than the creator's current source audience. Lowering audience is
always allowed. Raising audience above the creator's current authority SHALL be
denied. For conversational and tool-created reminders, omitted `audience`
SHALL resolve to the creating channel/session audience before persistence.

#### Scenario: Equal audience reminder allowed

- **GIVEN** the current session source audience is `Team`
- **WHEN** the creator saves a reminder with `audience: Team`
- **THEN** ACL reminder minting authorization allows the write

#### Scenario: Lower audience reminder allowed

- **GIVEN** the current session source audience is `Personal`
- **WHEN** the creator saves a reminder with `audience: Public`
- **THEN** ACL reminder minting authorization allows the write

#### Scenario: Higher audience reminder denied

- **GIVEN** the current session source audience is `Public`
- **WHEN** the creator saves a reminder with `audience: Team`
- **THEN** ACL reminder minting authorization denies the write
- **AND** the denial reason states that the requested audience exceeds the creator's authority

#### Scenario: Omitted conversational audience resolves from source

- **GIVEN** a reminder is being created from a Slack session with source audience `Team`
- **WHEN** the request omits `audience`
- **THEN** the effective reminder audience is resolved to `Team` before persistence

#### Scenario: Import path validates serialized audience

- **GIVEN** an authenticated import request carries a serialized reminder definition with `audience: Personal`
- **AND** the import caller's source audience is `Team`
- **WHEN** the server validates the reminder definition
- **THEN** the import is denied before persistence
- **AND** no over-privileged reminder is stored
