## ADDED Requirements

### Requirement: Reentrant init pre-population

`netclaw init` SHALL load existing `netclaw.json` and `secrets.json` at
entry and assign the parsed top-level dictionary to
`WizardContext.ExistingConfig`. When the wizard runs over an existing
install, every step viewmodel implementing `ISectionEditor` SHALL pre-fill
non-secret UI fields from its slice in `ExistingConfig` and SHALL render
secret-bearing fields empty with the documented hint text indicating
whether the underlying secret is present. Steps that do not implement
`ISectionEditor` SHALL preserve their first-run behavior in this change.

#### Scenario: Provider step pre-fills from existing config

- **GIVEN** `netclaw.json` contains a configured `Providers.anthropic`
  entry
- **WHEN** `netclaw init` enters the Provider step
- **THEN** the provider list opens with `anthropic` as the focused
  selection
- **AND** any API key input renders empty with "configured — leave blank
  to keep" hint text
- **AND** the OAuth token expiry date displays as previously stored

#### Scenario: Identity step pre-fills from existing config

- **GIVEN** `netclaw.json` contains a previously-set agent name, user
  name, and timezone
- **WHEN** `netclaw init` enters the Identity step
- **THEN** each text field opens with the previously-set value as the
  default

#### Scenario: Security Posture step pre-fills from existing config

- **GIVEN** `netclaw.json` contains a previously-set deployment posture
- **WHEN** `netclaw init` enters the Security Posture step
- **THEN** the posture list opens with the previously-set posture as the
  focused selection

#### Scenario: Fresh install leaves ExistingConfig null

- **GIVEN** no `netclaw.json` exists on disk
- **WHEN** `netclaw init` enters the wizard
- **THEN** `WizardContext.ExistingConfig` is `null`
- **AND** every step renders its first-run defaults

### Requirement: Merge-on-save for init wizard

`netclaw init` SHALL produce its terminal `netclaw.json` write as a merge
of the wizard's accumulated contributions over the existing on-disk file
(or a fresh skeleton when no file exists). For every top-level section
the wizard did not contribute to, the resulting file SHALL be
byte-identical to its pre-write state. The same merge rule SHALL apply
to `secrets.json`.

#### Scenario: Re-running init preserves unrelated sections

- **GIVEN** `netclaw.json` contains configured `Slack`, `Discord`, and
  `Search` sections
- **AND** `netclaw init` is re-run and only the Provider step is
  modified
- **WHEN** the wizard completes and writes
- **THEN** the resulting `netclaw.json` contains the updated `Providers`
  section
- **AND** `Slack`, `Discord`, and `Search` are byte-identical to their
  pre-write state

#### Scenario: Re-running init preserves unrelated secrets

- **GIVEN** `secrets.json` contains a Brave API key and Slack bot/app
  tokens
- **AND** `netclaw init` is re-run and only the Provider step's API key
  is changed
- **WHEN** the wizard completes and writes
- **THEN** the resulting `secrets.json` contains the new provider API key
- **AND** the Brave API key and Slack tokens are byte-identical to their
  pre-write state

#### Scenario: First-run write produces a complete file

- **GIVEN** no `netclaw.json` exists on disk
- **WHEN** the wizard completes and writes
- **THEN** the resulting `netclaw.json` contains every section the
  wizard contributed to
- **AND** validates against `netclaw-config.v1.schema.json`

### Requirement: Secrets never rehydrate to the wizard UI

No step in `netclaw init` SHALL display the decrypted value of any
secret stored in `secrets.json`. Secret-bearing inputs SHALL render
empty masked fields whose hint text indicates whether a value exists,
following the secret-handling contract defined in the
`section-editor-abstraction` capability.

#### Scenario: Re-run shows stored API key as configured-not-displayed

- **GIVEN** `secrets.json` contains a stored Brave API key
- **WHEN** `netclaw init` is re-run and reaches a step that would render
  the API key field
- **THEN** the field renders empty
- **AND** the hint text reads "configured — leave blank to keep"
- **AND** no part of the decrypted key appears anywhere on screen

#### Scenario: Re-run with blank submit preserves the stored secret

- **GIVEN** `secrets.json` contains a stored Brave API key
- **WHEN** `netclaw init` is re-run and the user leaves the API key
  field blank and continues
- **THEN** the wizard's terminal write does not rewrite the stored
  encrypted value
- **AND** the Brave API key is byte-identical in `secrets.json`
  pre-write and post-write
