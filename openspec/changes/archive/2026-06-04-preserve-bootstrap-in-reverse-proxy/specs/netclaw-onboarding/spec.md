## ADDED Requirements

### Requirement: Onboarding bootstrap aligns with daemon-owned first-launch bootstrap

The init wizard SHALL remain compatible with daemon-owned first-launch bootstrap seeding. Wizard-written bootstrap state SHALL NOT be required for first-launch success, and wizard finalization SHALL NOT overwrite an existing daemon-owned bootstrap credential.

#### Scenario: Wizardless first boot still succeeds

- **GIVEN** the operator never ran `netclaw init`
- **AND** daemon config is otherwise valid for a remote-auth-required exposure mode
- **WHEN** the daemon starts for the first time
- **THEN** first-launch bootstrap behavior does not depend on wizard-written device state

#### Scenario: Wizard bootstrap does not overwrite existing daemon-owned state

- **GIVEN** the daemon already seeded a bootstrap paired device/token
- **WHEN** the operator later runs `netclaw init`
- **THEN** wizard finalization does not overwrite the existing bootstrap credential automatically
