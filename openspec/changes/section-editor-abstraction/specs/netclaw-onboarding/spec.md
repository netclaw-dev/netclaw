## ADDED Requirements

### Requirement: Init-owned editor re-entry SHALL use existing config state

Init-owned editor re-entry SHALL load existing config into
`WizardContext.ExistingConfig` when `netclaw init` reuses a registered leaf
editor against an existing install, and SHALL prefill non-secret values from
that state. Secret-bearing fields SHALL remain masked and empty.

#### Scenario: Provider re-entry keeps credential field masked

- **GIVEN** an existing provider configuration with stored credentials
- **WHEN** an init-owned provider flow re-enters
- **THEN** provider choice and non-secret fields are prefilled
- **AND** credential inputs remain blank with configured/not-set hint text

#### Scenario: Identity re-entry prefills init-owned fields

- **GIVEN** an existing install with agent name, operator name, and
  timezone already set
- **WHEN** an init-owned identity flow re-enters
- **THEN** those non-secret fields are prefilled

### Requirement: Init-owned writes use semantic merge

Init-owned editor flows SHALL write changes through semantic merge-on-save.
Unrelated config meaning and unrelated stored secrets SHALL be preserved even
if the serialized file text changes.

#### Scenario: Identity-only edit preserves unrelated config meaning

- **GIVEN** an existing install with configured channels, search, and
  exposure settings
- **WHEN** an init-owned identity flow updates only identity-owned data
- **THEN** the unrelated config sections remain semantically unchanged

#### Scenario: Blank secret submission preserves existing secret

- **GIVEN** an init-owned flow includes a secret-bearing field with an
  existing stored value
- **WHEN** the operator leaves that field blank and saves
- **THEN** the existing secret remains stored
- **AND** no decrypted value is shown in the UI
