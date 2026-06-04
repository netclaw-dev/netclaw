# session-config-decomposition Specification

## Purpose

Decompose the monolithic `SessionConfig` and the `LlmSessionActor`'s large constructor into cohesive, separately-resolvable types. Runtime-derived model properties move into a standalone `ModelCapabilities` record, internal tuning constants move into a nested `SessionTuning` record, and the slimmed `SessionConfig` exposes only user-facing operational settings with `TimeSpan` timeouts. The Session configuration section is schema-validated with `additionalProperties: false`, and the session actor's dependencies are grouped into composite DI records to reduce constructor sprawl.
## Requirements
### Requirement: ModelCapabilities as standalone type

The system SHALL represent runtime-derived model properties in a `ModelCapabilities`
record separate from `SessionConfig`. Properties SHALL include `ModelId`,
`ContextWindowTokens`, `InputModalities`, and `OutputModalities`. The type SHALL be
registered as a DI singleton resolved from the model capability detection pipeline.

#### Scenario: ModelCapabilities registered independently from SessionConfig

- **GIVEN** the daemon starts and resolves model capabilities
- **WHEN** `ModelCapabilities` is registered in DI
- **THEN** `SessionConfig` does NOT contain `ModelId`, `ContextWindowTokens`,
  `InputModalities`, or `OutputModalities`
- **AND** `ModelCapabilities` is resolvable as a separate singleton

#### Scenario: CompactionTokenLimit computed from ModelCapabilities

- **GIVEN** `ModelCapabilities.ContextWindowTokens` is 32,768
- **AND** `SessionTuning.CompactionThreshold` is 0.75
- **WHEN** the compaction limit is calculated
- **THEN** the result is 24,576

#### Scenario: Default ModelCapabilities for unresolved models

- **GIVEN** model capability detection fails or returns no data
- **WHEN** default `ModelCapabilities` is created
- **THEN** `ContextWindowTokens` SHALL be 32,768
- **AND** `InputModalities` SHALL be `ModelModality.Text`
- **AND** `OutputModalities` SHALL be `ModelModality.Text`

### Requirement: SessionTuning for internal constants

The system SHALL represent internal tuning constants in a `SessionTuning` record
nested inside `SessionConfig` as `SessionConfig.Tuning`. Properties SHALL include
compaction settings (`CompactionThreshold`, `KeepRecentToolResults`,
`KeepRecentMessages`, `CompactionModelId`), tool retention settings
(`DiscoveredToolRetentionTurns`, `DiscoveredToolMaxCount`, `MaxInlineToolResultChars`),
snapshot interval (`SnapshotInterval`), and title generation interval
(`TitleGenerationInterval`). Feature flags (`MemorySidecarsEnabled`,
`DeterministicRetrievalEnabled`) SHALL be included for backward compatibility with
intent to remove.

#### Scenario: SessionTuning defaults match current production values

- **WHEN** a default `SessionTuning` is constructed
- **THEN** `CompactionThreshold` is 0.75
- **AND** `SnapshotInterval` is 20
- **AND** `KeepRecentToolResults` is 3
- **AND** `MaxInlineToolResultChars` is 12,000
- **AND** `DiscoveredToolRetentionTurns` is 3
- **AND** `DiscoveredToolMaxCount` is 12
- **AND** `KeepRecentMessages` is 6
- **AND** `TitleGenerationInterval` is 10
- **AND** `MemorySidecarsEnabled` is true
- **AND** `DeterministicRetrievalEnabled` is true

#### Scenario: SessionTuning bindable from config for testing

- **GIVEN** `netclaw.json` contains `"Session": { "Tuning": { "SnapshotInterval": 5 } }`
- **WHEN** configuration is bound
- **THEN** `SessionConfig.Tuning.SnapshotInterval` is 5
- **AND** all other tuning properties retain defaults

### Requirement: Slimmed SessionConfig with TimeSpan timeouts

The system SHALL represent user-facing operational settings in `SessionConfig`
using `TimeSpan` properties for timeouts (`TurnLlmTimeout`,
`ToolExecutionTimeout`, `SidecarLlmTimeout`) instead of `int` seconds.
Config-file JSON keys SHALL remain as `XxxTimeoutSeconds` (int) for
user-facing backward compatibility. A static bind method SHALL convert from the
raw int-seconds JSON representation to `TimeSpan`, enforcing a minimum of 1
second per timeout.

#### Scenario: TimeSpan conversion from config file

- **GIVEN** `netclaw.json` contains `"Session": { "TurnLlmTimeoutSeconds": 120 }`
- **WHEN** `SessionConfig` is bound from configuration
- **THEN** `SessionConfig.TurnLlmTimeout` is `TimeSpan.FromSeconds(120)`

#### Scenario: Minimum timeout enforcement

- **GIVEN** `netclaw.json` contains `"Session": { "SidecarLlmTimeoutSeconds": 0 }`
- **WHEN** `SessionConfig` is bound from configuration
- **THEN** `SessionConfig.SidecarLlmTimeout` is `TimeSpan.FromSeconds(1)`

#### Scenario: Default SessionConfig values

- **WHEN** a default `SessionConfig` is constructed
- **THEN** `IdleTimeout` is 30 minutes
- **AND** `MaxToolIterationsPerTurn` is 60
- **AND** `MemoryObserverIdleSeconds` is 90
- **AND** `TurnLlmTimeout` is 3 minutes
- **AND** `ToolExecutionTimeout` is 90 seconds
- **AND** `SidecarLlmTimeout` is 90 seconds

### Requirement: JSON schema validation for Session section

The `netclaw-config.v1.schema.json` Session section SHALL use
`additionalProperties: false` with explicit property definitions. Unknown
properties in the Session section SHALL be rejected by schema validation.

The Session schema SHALL define `MaxToolIterationsPerTurn` and SHALL NOT define
`MaxToolCallsPerTurn`.

#### Scenario: Valid Session config passes schema validation

- **GIVEN** a `netclaw.json` with `"Session": { "MaxToolIterationsPerTurn": 50 }`
- **WHEN** schema validation runs
- **THEN** validation passes

#### Scenario: Stale tool-call limit property rejected

- **GIVEN** a `netclaw.json` with `"Session": { "MaxToolCallsPerTurn": 30 }`
- **WHEN** schema validation runs
- **THEN** validation fails with an error identifying the unknown property

### Requirement: Composite dependency records for session actor

The system SHALL group `LlmSessionActor` constructor dependencies into composite
records: `SessionServices` (core runtime), `SessionToolServices` (tool execution,
nullable), `SessionMemoryServices` (memory infrastructure), and
`SessionObservability` (metrics and lifecycle). Each record SHALL be registered in
DI as a singleton and resolved automatically by Akka.Hosting's `resolver.Props<>()`.

#### Scenario: LlmSessionActor constructor accepts composite records

- **GIVEN** the DI container has registered `SessionServices`, `SessionToolServices`,
  `SessionMemoryServices`, `SessionObservability`, `ModelCapabilities`, and `SessionConfig`
- **WHEN** `resolver.Props<LlmSessionActor>(entityId)` is called
- **THEN** the actor is created with the composite records resolved from DI
- **AND** the constructor has ~7 parameters instead of 19

#### Scenario: Tool-less session with null SessionToolServices

- **GIVEN** no `IToolExecutor` is registered in DI
- **WHEN** `SessionToolServices` is resolved
- **THEN** it is null
- **AND** the session actor operates without tool execution capability

