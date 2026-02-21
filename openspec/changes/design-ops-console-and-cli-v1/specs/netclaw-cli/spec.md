## ADDED Requirements

### Requirement: Config and ACL validation

The CLI SHALL validate configuration and return actionable errors.

#### Scenario: Validation failure

- **WHEN** config validation fails
- **THEN** command exits non-zero
- **AND** output includes remediation guidance

### Requirement: Security diagnostics

The CLI SHALL report exposure mode and policy health.

#### Scenario: Doctor output

- **WHEN** operator runs `netclaw gateway doctor`
- **THEN** output includes exposure mode, policy status, and prioritized issues
