## MODIFIED Requirements

### Requirement: Subagent model role convention

When explicit `model` is absent, subagents SHALL preserve existing
`ModelRole`-based selection defaults and override behavior.

Subagent definitions MAY also specify an explicit `model` string (from
frontmatter) that references a named model/client registry entry. When explicit
`model` is present, the system SHALL use explicit model selection and SHALL NOT
use `ModelRole` for that subagent invocation.

When both `model` and `ModelRole` are set, `model` SHALL take precedence.

If explicit `model` cannot be resolved against the named model/client registry,
subagent load/startup SHALL fail deterministically and SHALL NOT silently fall
back to `ModelRole`.

#### Scenario: Existing model-role behavior remains when explicit model is absent

- **GIVEN** a subagent definition does not set explicit `model`
- **WHEN** a subagent is loaded and spawned
- **THEN** the system uses the existing `ModelRole` selection behavior unchanged

#### Scenario: Explicit model selects named registry entry

- **GIVEN** a subagent definition sets `model: summarize-fast`
- **AND** `summarize-fast` exists in the named model/client registry
- **WHEN** the subagent is loaded and spawned
- **THEN** the subagent uses the resolved named model/client entry

#### Scenario: Explicit model wins over model role

- **GIVEN** a subagent definition sets both `model: summarize-fast` and
  `modelRole: Compaction`
- **AND** `summarize-fast` exists in the named model/client registry
- **WHEN** the subagent is loaded and spawned
- **THEN** the explicit `model` selection is used
- **AND** `modelRole` is not used for model selection on that subagent

#### Scenario: Unresolved explicit model fails loudly without fallback

- **GIVEN** a subagent definition sets `model: typo-model-name`
- **AND** `typo-model-name` does not exist in the named model/client registry
- **WHEN** subagent definitions are loaded at startup or reload boundary
- **THEN** load/startup fails with a deterministic unresolved-model error
- **AND** the system does not silently fall back to `modelRole`
