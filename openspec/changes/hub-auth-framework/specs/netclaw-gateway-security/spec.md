## MODIFIED Requirements

### Requirement: Privileged action approval

The system SHALL require explicit approval for privileged operations. The
requesting connection's authenticated `PrincipalClassification` SHALL be used
to determine whether the caller has sufficient privilege to request or approve
privileged actions.

#### Scenario: Privileged request requires approval

- **WHEN** a privileged operation is requested
- **THEN** the system requires trusted operator approval before execution

#### Scenario: Operator principal can approve privileged actions

- **GIVEN** the requesting connection is authenticated as `Operator`
- **WHEN** a privileged action requires approval
- **THEN** the system accepts approval from this connection

#### Scenario: Non-operator principal cannot approve privileged actions

- **GIVEN** the requesting connection is authenticated as `TrustedInternal`
  or lower
- **WHEN** a privileged action requires approval
- **THEN** the system rejects the approval attempt
