# section-editor-abstraction Specification

## Purpose

Define the reusable CLI leaf-editor contract shared by bootstrap-only init
flows and future post-install config flows, including semantic persistence,
secret-safe re-entry, and audit obligations.

## Requirements

### Requirement: Leaf editor interface

The CLI SHALL define an `ISectionEditor` contract for reusable editable
leaf surfaces. A leaf editor SHALL declare a stable `SectionId`, a
user-facing `DisplayName`, optional `Category`, `ShowInMenu`, status and
summary methods, relevant validation checks, and a factory that returns an
`IWizardStepViewModel` runnable in either init-owned flows or config-owned
single-step hosting.

The contract SHALL describe leaf editing only. It SHALL NOT imply that the
top-level `netclaw config` IA is flat or identical to registry order.

#### Scenario: Registered leaf editor does not define dashboard shape

- **GIVEN** a registered leaf editor with `SectionId = "Search"`
- **WHEN** the config dashboard is later composed
- **THEN** the dashboard MAY place that leaf under a grouped page such as
  `Search` or `Security & Access`
- **AND** the leaf editor contract remains valid regardless of the
  top-level navigation shape

#### Scenario: Synthetic init-owned editor is allowed

- **GIVEN** an editor such as `Identity` spans generated files and config
  leaves
- **WHEN** it is registered with `ShowInMenu = false`
- **THEN** it MAY use a synthetic identifier when documented in the
  exemption list
- **AND** it SHALL remain absent from the config dashboard menu

### Requirement: Semantic merge-on-save

Leaf editors SHALL persist changes through semantic merge-on-save. The merge
writer SHALL preserve unrelated sections and inactive values semantically.
Formatting, property order, and byte-for-byte file identity are NOT part of
the contract.

#### Scenario: Editing one leaf preserves unrelated meaning

- **GIVEN** `netclaw.json` contains configured `Providers`, `Slack`,
  `Search`, and inactive exposure-mode values for modes other than the
  current `Daemon.ExposureMode`
- **WHEN** the operator edits only the Search leaf and saves
- **THEN** `Search` reflects the requested change
- **AND** the unrelated sections and inactive exposure-mode values remain
  semantically unchanged

#### Scenario: No-op save may rewrite formatting without changing meaning

- **GIVEN** an existing config file with non-canonical property order
- **WHEN** an editor performs a no-op save
- **THEN** the resulting file MAY differ in byte representation
- **AND** the resulting parsed config SHALL be semantically equivalent to
  the original

### Requirement: Reentrancy contract for init-owned flows

Init-owned re-entry flows SHALL prefill non-secret fields from
`WizardContext.ExistingConfig` when they reuse a leaf editor against existing
state. Secret-bearing fields SHALL remain empty and masked, using
existence-only hint text.

#### Scenario: Existing non-secret values prefill

- **GIVEN** an init-owned flow enters the Security Posture editor with an
  existing posture already configured
- **WHEN** the editor loads
- **THEN** the current posture is preselected

#### Scenario: Stored secrets never rehydrate

- **GIVEN** an editor owns a secret-bearing field whose value exists in
  `secrets.json`
- **WHEN** the editor loads
- **THEN** the field renders empty
- **AND** the hint indicates only whether a value exists
- **AND** the decrypted value is never displayed

### Requirement: Secret-presence lookup without decryption

`ConfigFileHelper` SHALL expose an existence-only secret lookup API used by
leaf editors to decide between "configured - leave blank to keep" and
"(not set)".

#### Scenario: Presence lookup does not decrypt

- **GIVEN** `secrets.json` contains an encrypted value for a leaf editor
- **WHEN** `SecretPresent(...)` is called
- **THEN** the result indicates presence or absence only
- **AND** the decrypted value is not materialized for UI display

### Requirement: Audit applies to registered leaf editors

The test project SHALL audit registered leaf editors for round-trip test
coverage and declared validation checks. `ShowInMenu = false` leaves remain
subject to round-trip coverage but are exempt from config-dashboard tape
requirements.

#### Scenario: Menu-hidden init-owned editor still needs a round-trip test

- **GIVEN** `Identity` is registered with `ShowInMenu = false`
- **WHEN** the registry audit runs
- **THEN** the audit requires a leaf-editor round-trip test class
- **AND** it does NOT require a config-dashboard smoke tape for Identity
