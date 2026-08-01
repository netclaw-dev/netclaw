## ADDED Requirements

### Requirement: Named model runtime registry

The daemon SHALL expose one runtime registry for all configured `Models.Definitions` entries.
The registry SHALL resolve names without case sensitivity and SHALL return the model reference, one composed `IChatClient`, and effective model capabilities.
The registry SHALL cache each composed client and capability result by canonical definition name.

#### Scenario: Resolve a named definition

- **GIVEN** `Models.Definitions` contains `vision-small`
- **WHEN** a runtime component requests `vision-small`
- **THEN** the registry SHALL return its model reference, composed client, and effective capabilities
- **AND** a later request with different name case SHALL return the same cached runtime entry

#### Scenario: Unknown definition fails visibly

- **GIVEN** no model definition matches a requested name
- **WHEN** a runtime component requests that name
- **THEN** the registry SHALL fail with an error that identifies the unknown name
- **AND** it SHALL NOT select a role model as a fallback

### Requirement: Role API uses named runtime entries

For named model configuration, the current role-based chat client provider SHALL resolve its role assignments through the named registry.
Main and compaction behavior SHALL remain unchanged.
Fallback SHALL remain limited to the current main-to-fallback provider error policy.

#### Scenario: Main role resolves through the registry

- **GIVEN** `Models.Roles.Main` references `main-text`
- **WHEN** the session actor requests the main role client
- **THEN** the role provider SHALL use the cached `main-text` registry entry

#### Scenario: Image proxy does not enter role fallback

- **GIVEN** `Models.Proxies.Image` references `vision-small`
- **WHEN** the main provider fails
- **THEN** the role router SHALL NOT use `vision-small` as a fallback model
