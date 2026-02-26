## MODIFIED Requirements

### Requirement: Guided onboarding

The CLI SHALL provide guided setup through `netclaw init`.

#### Scenario: Resume setup

- **GIVEN** onboarding is incomplete
- **WHEN** operator runs `netclaw init --resume`
- **THEN** setup continues from first incomplete step
