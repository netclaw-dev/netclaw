# netclaw-onboarding Specification

## Purpose

Define first-run and resumable onboarding experience for Netclaw operators.

## Requirements

### Requirement: Stepwise setup wizard

The system SHALL guide operators through setup steps with validation at each
step.

#### Scenario: Step progression

- **WHEN** operator completes a step successfully
- **THEN** onboarding advances to the next step

### Requirement: Secret-safe input handling

The system SHALL avoid echoing sensitive credentials in plain text output.

#### Scenario: Entering provider key

- **WHEN** operator enters a provider API key
- **THEN** the input is masked and not logged in clear text

### Requirement: Security warnings for public modes

The system SHALL show explicit warnings before enabling public exposure modes.

#### Scenario: Enable funnel mode

- **WHEN** operator selects `tailscale-funnel`
- **THEN** onboarding requires explicit confirmation and auth policy validation
