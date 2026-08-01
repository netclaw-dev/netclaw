## ADDED Requirements

### Requirement: Image proxy model controls

The model command and interactive model manager SHALL let the operator set or clear the optional image proxy.
The command surface SHALL support `netclaw model set image-proxy <provider> <model-id>` and `netclaw model clear image-proxy`.
The list surface SHALL show the effective named definition.

The save path SHALL reuse an existing matching model definition when possible.
It SHALL preserve existing model metadata.
It SHALL reject an unresolved provider, model, or definition before persistence.

#### Scenario: CLI assigns an image proxy

- **GIVEN** the provider and model are valid
- **WHEN** the operator runs `netclaw model set image-proxy <provider> <model-id>`
- **THEN** the command SHALL write `Models.Proxies.Image` as a named definition reference
- **AND** it SHALL preserve all unrelated definitions, roles, and metadata

#### Scenario: CLI clears an image proxy

- **GIVEN** an image proxy is configured
- **WHEN** the operator runs `netclaw model clear image-proxy`
- **THEN** the command SHALL remove only the image proxy assignment
- **AND** it SHALL preserve the referenced definition

#### Scenario: Interactive manager assigns and clears the proxy

- **GIVEN** the operator opens the interactive model manager
- **WHEN** the operator assigns or clears the image proxy
- **THEN** the manager SHALL use the same validated save path as the CLI
- **AND** it SHALL show a visible success or failure result

#### Scenario: Invalid proxy selection does not persist

- **GIVEN** the selected provider, model, or named reference is invalid
- **WHEN** the CLI or TUI save path validates the selection
- **THEN** the save SHALL fail before the configuration file changes

#### Scenario: Legacy model configuration migrates on proxy assignment

- **GIVEN** the configuration uses legacy inline model roles
- **WHEN** the operator assigns an image proxy
- **THEN** the save path SHALL convert all roles to the named shape
- **AND** it SHALL preserve their model metadata
