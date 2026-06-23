## ADDED Requirements

### Requirement: Config leaves use validated Netclaw UI components for mutable actions

Every mutable `netclaw config` leaf editor SHALL use page-independent
validated Netclaw UI components for text fields, toggles, pickers, add/remove
actions, reset actions, token rotation, and other completed actions. Config
pages SHALL NOT call persistence APIs directly from page key handlers.

#### Scenario: Skill Sources local path validates through the user action path

- **GIVEN** the operator opens Skill Sources and chooses Add local folder
- **WHEN** the operator types a missing path and presses `Enter`
- **THEN** the validated component runs static path validation
- **AND** the config file remains unchanged
- **AND** the UI shows the validation error

#### Scenario: Skill Sources remote URL probes through the user action path

- **GIVEN** the operator opens Skill Sources and chooses Add skill server
- **AND** the fake skill feed probe is configured to fail
- **WHEN** the operator types a structurally valid URL and presses `Enter`
- **THEN** the validated component runs dynamic validation through the commit
  pipeline
- **AND** persistence is blocked before writing
- **AND** the UI exposes save-anyway only through the declared failure policy

### Requirement: Config autosave and explicit acceptance share one pipeline

Config completed actions SHALL use the same `NetclawUiCommitPipeline` whether
the action is accepted by `Enter`, a save/apply affordance, a toggle, picker
selection, or autosave trigger. Autosave SHALL NOT have a separate persistence
path.

#### Scenario: Toggle autosave uses dynamic validation when declared

- **GIVEN** a config toggle changes a runtime-consumed setting whose commit
  declares dynamic validation
- **WHEN** the operator toggles the setting
- **THEN** the autosave trigger runs static and dynamic validation through the
  commit pipeline before persistence

#### Scenario: Escape never persists incomplete drafts

- **GIVEN** the operator has typed a draft text value in a config leaf
- **AND** the draft has not been accepted by `Enter` or an equivalent Apply
  action
- **WHEN** the operator presses `Esc`
- **THEN** the draft is canceled or navigation occurs
- **AND** no config, secrets, or sidecar file is modified

### Requirement: Config validation coverage is driven by standard component contracts

Every migrated config leaf SHALL have headless tests that drive the same input
path the user drives. Tests SHALL cover typed input, paste when supported,
`Enter` acceptance, `Esc` cancellation, static validation failure, dynamic
validation failure when declared, unchanged persistence on failure, and
successful canonical persistence.

#### Scenario: Audit fails when a config leaf lacks interaction-path validation tests

- **WHEN** the config editor audit runs
- **THEN** each visible mutable config leaf must identify tests that exercise
  the validated component user-action path
- **AND** a leaf with only direct view-model save tests fails the audit

#### Scenario: Runtime consumer proof remains required

- **GIVEN** a config leaf writes values consumed by daemon startup, routing,
  ACL, channel adapters, skill scanners, search providers, or webhook runtime
- **WHEN** the leaf is migrated to validated components
- **THEN** tests prove the persisted canonical representation is consumed by
  the runtime-facing consumer
