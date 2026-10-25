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
- The thinking level SHALL be one that the provider's thinking dialect
  supports. The provider requirement "Unsupported thinking level is rejected"
  defines the error.
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
- **WHEN** the operator selects thinking level `low` and saves
- **THEN** the manager SHALL show the supported levels
- **AND** `netclaw.json` SHALL stay unchanged

#### Scenario: TUI save persists a valid value

- **GIVEN** the TUI model manager shows a definition
- **WHEN** the operator types `32000` in the output limit field and saves
- **THEN** the definition SHALL hold `MaxOutputTokens` 32000

#### Scenario: Doctor reports an invalid persisted value

- **GIVEN** `netclaw.json` holds `MaxOutputTokens` -1 for a definition
- **WHEN** the operator runs `netclaw doctor`
- **THEN** doctor SHALL report an error with the key path
- **AND** `netclaw doctor --fix` SHALL NOT delete or replace the value
