## ADDED Requirements

### Requirement: set_working_directory tool audience gating

The `set_working_directory` tool SHALL be profile-managed. It SHALL NOT
be exposed to Public or Team audiences by default. Personal audience
sessions SHALL have access. Custom audience profiles MAY add
`set_working_directory` to their `AllowedTools` list to grant access.

#### Scenario: set_working_directory not exposed to public audience

- **GIVEN** the default audience profile configuration
- **WHEN** tool exposure is computed for a public audience session
- **THEN** `set_working_directory` is not in the exposed tool list

#### Scenario: set_working_directory not exposed to team audience

- **GIVEN** the default audience profile configuration
- **WHEN** tool exposure is computed for a team audience session
- **THEN** `set_working_directory` is not in the exposed tool list

#### Scenario: set_working_directory exposed to personal audience

- **GIVEN** the default audience profile configuration
- **WHEN** tool exposure is computed for a personal audience session
- **THEN** `set_working_directory` is in the exposed tool list
