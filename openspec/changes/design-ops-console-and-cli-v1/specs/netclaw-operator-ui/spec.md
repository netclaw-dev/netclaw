## ADDED Requirements

### Requirement: Operations-first overview

The UI SHALL provide a dense overview of runtime and security state.

#### Scenario: Overview dashboard load

- **WHEN** an operator opens the overview
- **THEN** gateway health, Slack status, persistence status, and policy deny
  counters are visible

### Requirement: Policy editing with validation

The UI SHALL validate ACL changes before apply.

#### Scenario: Invalid ACL edit

- **WHEN** operator enters invalid ACL JSON
- **THEN** apply is blocked
