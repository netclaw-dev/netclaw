## ADDED Requirements

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
- **AND** all features are enabled by default

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
`ContributeConfig()`.

#### Scenario: Disabled memory writes Enabled false

- **GIVEN** the operator disabled memory in Feature Selection
- **WHEN** config is finalized
- **THEN** `Memory.Enabled` is `false` in `netclaw.json`

#### Scenario: Disabled search writes Enabled false

- **GIVEN** the operator disabled search in Feature Selection
- **WHEN** config is finalized
- **THEN** `Search.Enabled` is `false` in `netclaw.json`

#### Scenario: Disabled scheduling writes top-level Scheduling.Enabled false

- **GIVEN** the operator disabled scheduling in Feature Selection
- **WHEN** config is finalized
- **THEN** `Scheduling.Enabled` is `false` in `netclaw.json`
- **AND** `Scheduling` contains no other properties in this change

#### Scenario: Default Personal config has all features enabled

- **GIVEN** the operator selected Personal posture (Feature Selection skipped)
- **WHEN** config is finalized
- **THEN** all `Enabled` flags default to `true`

### Requirement: Feature flags respected at runtime

Runtime subsystems SHALL check their respective `Enabled` config flag before
activating. When a feature is disabled via config, it SHALL be inactive
regardless of audience profile. When a feature is enabled at runtime, audience
profiles still control which audiences may discover or use it.

#### Scenario: Memory disabled in config suppresses recall

- **GIVEN** `Memory.Enabled` is `false` in config
- **WHEN** a Team-audience session starts a new turn
- **THEN** automatic recall returns an empty result
- **AND** memory tools are not offered to the LLM

#### Scenario: Memory enabled in config allows recall

- **GIVEN** `Memory.Enabled` is `true` in config
- **WHEN** a Personal-audience session starts a new turn
- **THEN** automatic recall executes normally

#### Scenario: Search runtime enabled but Public audience not allowlisted

- **GIVEN** `Search.Enabled` is `true` in config
- **AND** the Public audience profile does not explicitly allow `web_search` or
  `web_fetch`
- **WHEN** a Public session starts
- **THEN** search runtime may exist for the deployment
- **BUT** `web_search` and `web_fetch` are not exposed to that session
