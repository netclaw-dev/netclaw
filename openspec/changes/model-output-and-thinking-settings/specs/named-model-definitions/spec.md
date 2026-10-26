## ADDED Requirements

### Requirement: Definition-owned output limit and thinking level

A named model definition SHALL own two optional, provider-neutral settings: the
output limit (`MaxOutputTokens`, a positive integer) and the thinking level
(`Thinking`, one of `Off`, `Low`, `Medium`, `High`, `Max`). A role SHALL NOT
hold these settings. The persisted thinking level SHALL use the canonical enum
name. An absent setting SHALL mean "no model value". Use the
[engineering glossary](../../../../../docs/spec/GLOSSARY.md) for "thinking
level" and "thinking dialect".

#### Scenario: Settings follow the definition across role switches

- **GIVEN** definition `sonnet` has `MaxOutputTokens` 32000 and `Thinking` `Low`
- **WHEN** Main switches from `sonnet` to another definition and back
- **THEN** the `sonnet` definition SHALL keep both settings
- **AND** Main SHALL resolve to 32000 and `Low`

#### Scenario: Two definitions of one model hold different levels

- **GIVEN** definitions `sonnet-low` and `sonnet-high` name the same provider and model ID
- **AND** their `Thinking` values are `Low` and `High`
- **WHEN** Main references `sonnet-low` and Compaction references `sonnet-high`
- **THEN** the Main role SHALL resolve to `Low`
- **AND** the Compaction role SHALL resolve to `High`

#### Scenario: Absent settings stay absent

- **GIVEN** a definition that has no `MaxOutputTokens` and no `Thinking`
- **WHEN** an operator assigns the definition to another role
- **THEN** the system SHALL persist no value for either setting

#### Scenario: Invalid persisted value fails loudly

- **GIVEN** a definition with `MaxOutputTokens` 0 or `Thinking` `Turbo`
- **WHEN** configuration is validated or loaded
- **THEN** validation SHALL fail with the key path of the bad value
- **AND** the daemon SHALL NOT replace the value with a default
