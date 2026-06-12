## ADDED Requirements

### Requirement: Webhook route creation inherits creator audience with escalation guard

Webhook route creation through the agent tool (`set_webhook`) SHALL default the route's audience to the creating session's execution-context audience when the caller does not specify one explicitly. The minting path SHALL reject a requested audience that exceeds the creator's authority (downgrade-only), mirroring reminder minting validation in `netclaw-scheduling`. Webhook routes defined directly in configuration (no creator context) SHALL retain `Public` as the fail-closed default. Execution SHALL continue to use the route's stored, validated audience as the session's source audience.

#### Scenario: Omitted audience inherits the creating context

- **GIVEN** a `set_webhook` invocation from a session whose execution audience is `Personal`
- **AND** no explicit audience argument is provided
- **WHEN** the webhook route is created
- **THEN** the persisted route audience is `Personal`

#### Scenario: Creator cannot mint a route above its own authority

- **GIVEN** a `set_webhook` invocation from a session whose execution audience is `Team`
- **WHEN** the caller requests audience `Personal`
- **THEN** the creation is rejected because the requested audience exceeds creator authority
- **AND** no route is persisted

#### Scenario: Explicit downgrade is allowed

- **GIVEN** a `set_webhook` invocation from a session whose execution audience is `Personal`
- **WHEN** the caller requests audience `Team`
- **THEN** the persisted route audience is `Team`

#### Scenario: Config-defined route retains fail-closed default

- **GIVEN** a webhook route defined directly in configuration with no audience specified
- **WHEN** the route is loaded
- **THEN** the route audience is `Public`
