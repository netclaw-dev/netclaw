## ADDED Requirements

### Requirement: Named model registry resolution for explicit subagent selection

The model provider subsystem SHALL expose named model/client registry
resolution for explicit model references supplied by other capabilities,
including subagent frontmatter `model` values.

Resolution SHALL be deterministic and exact by registry name.

#### Scenario: Explicit subagent model name resolves to registry entry

- **GIVEN** the named model/client registry contains `summarize-fast`
- **WHEN** subagent configuration resolution requests `summarize-fast`
- **THEN** the registry returns the corresponding model/client entry

#### Scenario: Unknown explicit subagent model name returns unresolved error

- **GIVEN** the named model/client registry does not contain `typo-model-name`
- **WHEN** subagent configuration resolution requests `typo-model-name`
- **THEN** resolution returns a deterministic unresolved-model error that
  includes the unknown name

### Requirement: Fail-loud validation for explicit subagent model references

Startup/load validation SHALL verify each explicit subagent `model` reference
against the named model/client registry.
Any unresolved explicit model reference SHALL fail startup/load, and the system
SHALL NOT silently degrade to role-based model selection for that subagent.
Diagnostics/doctor output MUST include unresolved model name and owning
subagent identifier so operators can remediate configuration errors.

#### Scenario: Startup fails on unresolved explicit subagent model

- **GIVEN** a loaded subagent definition references explicit `model` value
  `typo-model-name`
- **AND** no registry entry exists for `typo-model-name`
- **WHEN** startup/load validation runs
- **THEN** the validation phase fails and startup/load is rejected
- **AND** no silent fallback to role-based selection occurs

#### Scenario: Diagnostics report unresolved explicit model reference

- **GIVEN** startup/load validation finds unresolved explicit subagent model
  references
- **WHEN** operator inspects diagnostics or doctor output
- **THEN** output lists each unresolved model name and associated subagent
  identifier
