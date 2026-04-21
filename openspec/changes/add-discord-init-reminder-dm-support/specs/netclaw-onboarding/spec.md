## ADDED Requirements

### Requirement: Discord setup path in netclaw init pipeline

The guided `netclaw init` flow SHALL support Discord adapter onboarding in the
same pipeline as existing setup. When Discord is enabled, the wizard SHALL
collect required Discord connection credentials, validate required fields, and
write Discord adapter configuration to output config.

#### Scenario: Discord enabled during init writes adapter configuration

- **GIVEN** the operator enables Discord during `netclaw init`
- **WHEN** wizard input collection completes successfully
- **THEN** generated config includes Discord adapter connection settings
- **AND** startup-critical Discord fields are present and non-empty

#### Scenario: Missing required Discord credential blocks completion

- **GIVEN** the operator enables Discord during `netclaw init`
- **WHEN** a required Discord credential is missing or invalid
- **THEN** the wizard reports a validation error and does not finish setup
- **AND** it does not emit a partially enabled Discord config

### Requirement: Baseline Discord ACL generation in init

When Discord is enabled in onboarding, init output SHALL include baseline
default-deny ACL entries for Discord sender/channel authorization so behavior
matches Slack-like security posture out of the box.

#### Scenario: Init writes Discord default-deny ACL baseline

- **GIVEN** Discord is enabled in init wizard flow
- **WHEN** configuration files are generated
- **THEN** ACL config contains explicit Discord sender/channel policy entries
- **AND** there is no implicit allow behavior for unlisted Discord identities
