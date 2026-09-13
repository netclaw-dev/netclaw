# named-model-definitions Specification

## Purpose

Define named model definitions that own provider identity, model ID, and
operator capability overrides. Model roles reference a definition by name. A
role switch therefore does not destroy the metadata of the definition it leaves.
This capability also defines legacy configuration acceptance, migration, and
conflict behavior.

## Requirements

### Requirement: Model-owned named definitions
The system SHALL store provider identity, model ID, context-window override, modality overrides, and provenance in named model definitions independent of runtime role assignment. Roles SHALL reference definitions by name.

#### Scenario: Switching away and back preserves overrides
- **GIVEN** definition `vision` has a manual `InputModalities` override
- **WHEN** Main switches from `vision` to another definition and back
- **THEN** the `vision` definition SHALL remain unchanged
- **AND** Main SHALL resolve to its original override

#### Scenario: Manual absence remains runtime detection
- **GIVEN** an existing definition omits an optional capability property
- **WHEN** the definition is assigned to another role
- **THEN** the property SHALL remain absent
- **AND** no tombstone or discovered replacement SHALL be persisted

### Requirement: Legacy model configuration compatibility
The system SHALL accept the legacy inline Main/Fallback/Compaction shape without rewriting it during startup and SHALL resolve it to the same runtime model selection.

#### Scenario: Existing deployment starts after upgrade
- **GIVEN** a valid configuration written by the latest stable Netclaw image
- **WHEN** the upgraded daemon starts
- **THEN** startup SHALL succeed with equivalent model role values and capabilities
- **AND** the configuration file SHALL not be rewritten merely by startup

#### Scenario: Explicit mutation migrates legacy shape
- **GIVEN** a valid legacy configuration
- **WHEN** an operator performs a model-writing command or runs doctor fix
- **THEN** the system SHALL atomically persist the named shape before completing the mutation
- **AND** the persisted named shape SHALL resolve to the same runtime values

#### Scenario: Ambiguous shape fails loudly
- **GIVEN** configuration contains both legacy role objects and named role references
- **WHEN** configuration is validated or loaded
- **THEN** the operation SHALL fail with remediation identifying the mixed shape

### Requirement: Reference integrity
Every persisted role reference SHALL resolve to an existing definition before persistence and startup.

#### Scenario: Missing definition is rejected
- **WHEN** a role references an unknown definition
- **THEN** validation SHALL fail before runtime client construction
- **AND** no partial configuration write SHALL occur

#### Scenario: Conflicting legacy duplicates are rejected
- **GIVEN** two legacy roles identify the same provider/model but contain conflicting overrides
- **WHEN** migration is requested
- **THEN** migration SHALL fail with the conflicting roles and fields
- **AND** the legacy file SHALL remain unchanged
