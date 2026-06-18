## MODIFIED Requirements

### Requirement: Feature selection wizard step

The init wizard SHALL present a Feature Selection step after the Security
Posture step for non-Personal deployment postures. The step SHALL display
toggleable deployment-wide feature switches with audience-appropriate defaults.
These switches control runtime enablement, not audience exposure. Audience
exposure remains governed by explicit tool/server allowlists.

#### Scenario: Feature selection shown for Public posture

- **GIVEN** the operator selected Public deployment posture
- **WHEN** the Security Posture step completes
- **THEN** the next step is Feature Selection
- **AND** features default to: memory off, search off, skills off, scheduling
  off, subagents off, webhooks off

#### Scenario: Feature selection shown for Team posture

- **GIVEN** the operator selected Team deployment posture
- **WHEN** the Security Posture step completes
- **THEN** the next step is Feature Selection
- **AND** features default to: memory on, search on, skills on, scheduling on,
  subagents on, webhooks on

#### Scenario: Feature selection skipped for Personal posture

- **GIVEN** the operator selected Personal posture
- **WHEN** the Security Posture step completes
- **THEN** the Feature Selection step is skipped
- **AND** the wizard writes no per-feature `Enabled` flags to the config
- **AND** the runtime treats absent `Enabled` flags as `true` (schema default),
  so all features are effectively on without the wizard writing explicit values

#### Scenario: Operator toggles features

- **GIVEN** the Feature Selection step is displayed
- **WHEN** the operator presses Space on a feature row
- **THEN** the feature toggles between enabled and disabled
- **AND** pressing Enter advances to the next wizard step

#### Scenario: Public search toggle does not implicitly allowlist Public search tools

- **GIVEN** the operator selected Public deployment posture
- **AND** the operator enables Search in Feature Selection
- **WHEN** config is finalized
- **THEN** deployment-wide search runtime is enabled
- **BUT** `web_search` and `web_fetch` are still absent from Public sessions
  unless the operator explicitly allowlists them for the Public audience

### Requirement: Feature config Enabled flags

The configuration schema SHALL include `Enabled` boolean properties for
Memory, Search, SkillSync, SubAgents, and Webhooks sections, plus a new top-
level `Scheduling` section whose only property is `Enabled`. The Feature
Selection wizard step SHALL write these flags to the config during
`ContributeConfig()` only when the step actually runs (i.e., for non-Personal
postures). For Personal posture, `ContributeConfig()` is never called and no
`Enabled` flags are written; the runtime defaults missing flags to `true`.

These flags MAY be set during bootstrap and SHALL be editable post-install
through the `Enabled Features` leaf. The post-install editor and bootstrap
flow SHALL preserve config semantics for equivalent inputs; byte-identical
serialization is not required.

#### Scenario: Disabled memory writes Enabled false

- **GIVEN** the operator disabled memory in Feature Selection
- **WHEN** config is finalized
- **THEN** `Memory.Enabled` is `false` in `netclaw.json`

#### Scenario: Disabled search writes Enabled false

- **GIVEN** the operator disabled search in Feature Selection
- **WHEN** config is finalized
- **THEN** `Search.Enabled` is `false` in `netclaw.json`

#### Scenario: Enabled Features writes deployment-wide flags

- **GIVEN** the operator disables search in Enabled Features
- **WHEN** the editor saves
- **THEN** `Search.Enabled` is `false` in `netclaw.json`

#### Scenario: Disabled scheduling writes top-level Scheduling.Enabled false

- **GIVEN** the operator disabled scheduling in Feature Selection
- **WHEN** config is finalized
- **THEN** `Scheduling.Enabled` is `false` in `netclaw.json`
- **AND** `Scheduling` contains no other properties in this change

#### Scenario: Personal posture omits Enabled flags from config

- **GIVEN** the operator selected Personal posture (Feature Selection skipped)
- **WHEN** config is finalized
- **THEN** no per-feature `Enabled` flags are written to `netclaw.json`
- **AND** the runtime loads each absent flag as `true` via the default-true
  fallback in `LoadEnabledFeatures`, making all features effectively enabled

## ADDED Requirements

### Requirement: Post-install posture change opens Enabled Features editor

A non-Personal posture change applied in `netclaw config` SHALL open the Enabled Features editor.

When the operator applies a non-Personal posture change in `netclaw config`,
the Security & Access view SHALL immediately transition to the Enabled Features
editor after saving the posture, so the operator can review and adjust
deployment-wide feature gates without a separate navigation step.

#### Scenario: Non-Personal posture save transitions to Enabled Features

- **WHEN** the operator saves a posture change to Team or Public posture in
  `netclaw config -> Security & Access -> Security Posture`
- **THEN** the view transitions directly to the Enabled Features sub-editor
  (`SecurityAccessEditorMode.Features`)
- **AND** the Enabled Features editor reflects the current on-disk feature
  flag state (re-loaded from config after the posture save)

#### Scenario: Personal posture save returns to Security & Access menu

- **WHEN** the operator saves a posture change to Personal posture in
  `netclaw config -> Security & Access -> Security Posture`
- **THEN** the view returns to the Security & Access menu
  (`SecurityAccessEditorMode.Menu`) and does not open the Enabled Features
  editor
