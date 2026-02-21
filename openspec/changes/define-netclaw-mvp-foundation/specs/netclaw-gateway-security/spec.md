## ADDED Requirements

### Requirement: Default-deny policy

The system SHALL deny interactions unless explicitly allowed by ACL.

#### Scenario: Unknown sender blocked

- **WHEN** an unknown sender triggers an interaction
- **THEN** the interaction is denied

### Requirement: Controlled exposure modes

The system SHALL support explicit exposure modes with secure defaults.

#### Scenario: Default local mode

- **WHEN** no exposure mode is configured
- **THEN** the system binds loopback-only
