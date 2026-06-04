## MODIFIED Requirements

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
