## ADDED Requirements

### Requirement: Model output limit and thinking level editing

`netclaw model set` SHALL accept `--max-output-tokens <tokens>`,
`--clear-max-output-tokens`, `--thinking <off|low|medium|high|max>`, and
`--clear-thinking`. The TUI model manager SHALL show and edit the same two
settings for the selected definition. The CLI and the TUI SHALL use one writer
and one validator. Each SHALL reject an invalid value before persistence, and a
rejected value SHALL leave `netclaw.json` unchanged. The persisted value SHALL
be the value that the daemon reads at startup.

Validation rules:

- The output limit SHALL be a positive integer.
- The output limit SHALL be less than the definition's configured context
  window, when one is set.
- The output limit SHALL NOT be above the model's maximum output, when the
  provider probe reports one.
- The thinking level SHALL be an offered level. The provider requirements
  "Thinking capability of a model" and "Unsupported thinking level is
  rejected" define the offered levels and the error.
- A set flag and its clear flag SHALL NOT appear together.

#### Scenario: CLI saves both settings

- **WHEN** the operator runs `netclaw model set main openai gpt-5 --max-output-tokens 32000 --thinking low`
- **THEN** the selected definition SHALL hold `MaxOutputTokens` 32000 and `Thinking` `Low`
- **AND** a fresh configuration load SHALL resolve Main to 32000 and `Low`

#### Scenario: CLI rejects a non-positive limit before persistence

- **WHEN** the operator runs `netclaw model set main openai gpt-5 --max-output-tokens 0`
- **THEN** the command SHALL exit with a non-zero code
- **AND** `netclaw.json` SHALL stay unchanged

#### Scenario: CLI rejects a limit not below the context window

- **GIVEN** the definition has `ContextWindow` 32768
- **WHEN** the operator sets `--max-output-tokens 32768`
- **THEN** the command SHALL fail and SHALL name the context window
- **AND** `netclaw.json` SHALL stay unchanged

#### Scenario: Clear flags remove the settings

- **GIVEN** a definition with both settings
- **WHEN** the operator runs `model set` with `--clear-max-output-tokens --clear-thinking`
- **THEN** the definition SHALL hold neither setting

#### Scenario: Conflicting flags fail

- **WHEN** the operator passes `--thinking low` and `--clear-thinking` together
- **THEN** the command SHALL fail without a write

#### Scenario: TUI save uses the same validator

- **GIVEN** the TUI model manager shows a DeepSeek definition
- **WHEN** the operator opens the thinking picker
- **THEN** the picker SHALL grey out `medium` with its reason
- **AND** the operator SHALL NOT be able to save `medium`

#### Scenario: TUI save persists a valid value

- **GIVEN** the TUI model manager shows a definition
- **WHEN** the operator types `32000` in the output limit field and saves
- **THEN** the definition SHALL hold `MaxOutputTokens` 32000

#### Scenario: Doctor reports an invalid persisted value

- **GIVEN** `netclaw.json` holds `MaxOutputTokens` -1 for a definition
- **WHEN** the operator runs `netclaw doctor`
- **THEN** doctor SHALL report an error with the key path
- **AND** `netclaw doctor --fix` SHALL NOT delete or replace the value

#### Scenario: CLI rejects a limit above the known model maximum

- **GIVEN** the provider probe reports a maximum output of 16384 for the model
- **WHEN** the operator sets `--max-output-tokens 32000`
- **THEN** the command SHALL fail and SHALL name the model maximum
- **AND** `netclaw.json` SHALL stay unchanged

### Requirement: Model output limit and thinking level display

`netclaw model list` and the TUI role table SHALL show a "Max out" column and a
"Thinking" column for each role. An unset limit SHALL show as the default. In
the TUI model manager, `P` SHALL open a properties panel for the model
definition of the selected role. The panel SHALL edit the output limit and the
thinking level.

#### Scenario: List shows the configured and default values

- **GIVEN** Main has `MaxOutputTokens` 64000 and `Thinking` `Low`
- **AND** Compaction has neither setting
- **WHEN** the operator runs `netclaw model list`
- **THEN** the Main row SHALL show 64000 and `low`
- **AND** the Compaction row SHALL show the default limit and the default level

#### Scenario: Shared definition names its other roles

- **GIVEN** Main and Compaction reference the same definition
- **WHEN** the operator opens the properties panel from the Main row
- **THEN** the panel SHALL show "Also used by: Compaction"

#### Scenario: Endpoint without a dialect edits only the output limit

- **GIVEN** a vLLM provider without `ThinkingDialect`
- **WHEN** the operator opens the properties panel for one of its models
- **THEN** only the output limit field SHALL be editable
- **AND** the panel SHALL show the `ThinkingDialect` hint

#### Scenario: Unknown capability shows its reason

- **GIVEN** a model whose thinking capability is unknown, on a provider with a dialect
- **WHEN** the operator opens the thinking picker
- **THEN** the picker SHALL offer only `off`
- **AND** it SHALL show "thinking support unknown for this endpoint"
