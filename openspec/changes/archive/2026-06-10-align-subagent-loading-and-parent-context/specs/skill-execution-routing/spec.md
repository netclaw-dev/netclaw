## ADDED Requirements

### Requirement: Routed subagent execution uses live registry and parent context

When a skill activation resolves through `metadata.subagent`, the runtime SHALL
use the same reloadable subagent registry and the same immutable parent-context
snapshot contract as explicit `spawn_agent` execution.

#### Scenario: Routed activation picks up edited subagent definition

- **GIVEN** a skill routes through `metadata.subagent: operations-helper`
- **AND** `operations-helper.md` is edited to a new valid state on disk
- **WHEN** the next routed activation occurs
- **THEN** the runtime reloads the subagent registry before routing
- **AND** the routed activation uses the updated definition

#### Scenario: Routed activation fails closed after invalid edit

- **GIVEN** a skill routes through `metadata.subagent: operations-helper`
- **AND** `operations-helper.md` is edited into an invalid state on disk
- **WHEN** the next routed activation occurs
- **THEN** routing fails deterministically against the reloaded registry
- **AND** inline fallback is not attempted
- **AND** the stale prior definition is not used
