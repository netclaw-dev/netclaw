## MODIFIED Requirements

### Requirement: All authorized model-invocable skills visible in index

The system SHALL include every authorized model-invocable skill in the index regardless of source. It SHALL exclude skills with `DisableModelInvocation` and MCP prompt skills whose server is not allowed for the audience.

#### Scenario: All authorized logical skills visible without physical origins

- **GIVEN** accepted skills from system, native, server-feed, external, and MCP prompt sources
- **WHEN** the index is generated for an authorized audience
- **THEN** every model-invocable skill appears by logical name
- **AND** source paths are not required to use the skill

#### Scenario: MCP prompt signature appears

- **GIVEN** an allowed MCP prompt has one required and one optional argument
- **WHEN** the index is generated
- **THEN** the prompt skill appears under its canonical logical name
- **AND** its compact argument hint distinguishes required and optional values

#### Scenario: Skill without allowed-tools is visible

- **GIVEN** an authorized skill has no `allowed-tools` metadata
- **WHEN** the index is generated
- **THEN** the skill appears in the index
