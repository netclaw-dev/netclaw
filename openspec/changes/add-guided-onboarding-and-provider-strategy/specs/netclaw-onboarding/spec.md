## ADDED Requirements

### Requirement: Stepwise setup wizard

The system SHALL guide operators through setup steps with validation at each
step.

#### Scenario: Step progression

- **WHEN** operator completes a step successfully
- **THEN** onboarding advances to the next step

### Requirement: Security warnings for public modes

The system SHALL show explicit warnings before enabling public exposure modes.

#### Scenario: Enable funnel mode

- **WHEN** operator selects `tailscale-funnel`
- **THEN** onboarding requires explicit confirmation and auth policy validation
