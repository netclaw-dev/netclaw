## ADDED Requirements

### Requirement: Feature selection wizard step

The init wizard SHALL present a Feature Selection step after the Security
Posture step for non-Personal deployment postures. The step SHALL display
toggleable feature capabilities with audience-appropriate defaults.

#### Scenario: Feature selection shown for Public posture

- **GIVEN** the operator selected Public deployment posture
- **WHEN** the Security Posture step completes
- **THEN** the next step is Feature Selection
- **AND** features default to: memory off, skills off, scheduling off,
  subagents off, webhooks off, web search on

#### Scenario: Feature selection shown for Team posture

- **GIVEN** the operator selected Team deployment posture
- **WHEN** the Security Posture step completes
- **THEN** the next step is Feature Selection
- **AND** features default to: memory on, skills on, scheduling on,
  subagents on, webhooks on, web search on

#### Scenario: Feature selection skipped for Personal posture

- **GIVEN** the operator selected Personal deployment posture
- **WHEN** the Security Posture step completes
- **THEN** the Feature Selection step is skipped
- **AND** all features are enabled by default

#### Scenario: Operator toggles features

- **GIVEN** the Feature Selection step is displayed
- **WHEN** the operator presses Space on a feature row
- **THEN** the feature toggles between enabled and disabled
- **AND** pressing Enter advances to the next wizard step

### Requirement: Feature config Enabled flags

The configuration schema SHALL include `Enabled` boolean properties for
Memory, SkillSync, Scheduling, SubAgents, and Webhooks sections. The Feature
Selection wizard step SHALL write these flags to the config during
`ContributeConfig()`.

#### Scenario: Disabled memory writes Enabled false

- **GIVEN** the operator disabled memory in Feature Selection
- **WHEN** config is finalized
- **THEN** `Memory.Enabled` is `false` in `netclaw.json`

#### Scenario: Default Personal config has all features enabled

- **GIVEN** the operator selected Personal posture (Feature Selection skipped)
- **WHEN** config is finalized
- **THEN** all `Enabled` flags default to `true`

### Requirement: Feature flags respected at runtime

Runtime subsystems SHALL check their respective `Enabled` config flag before
activating. When a feature is disabled via config, it SHALL be inactive
regardless of audience profile.

#### Scenario: Memory disabled in config suppresses recall

- **GIVEN** `Memory.Enabled` is `false` in config
- **WHEN** a Team-audience session starts a new turn
- **THEN** automatic recall returns an empty result
- **AND** memory tools are not offered to the LLM

#### Scenario: Memory enabled in config allows recall

- **GIVEN** `Memory.Enabled` is `true` in config
- **WHEN** a Personal-audience session starts a new turn
- **THEN** automatic recall executes normally
