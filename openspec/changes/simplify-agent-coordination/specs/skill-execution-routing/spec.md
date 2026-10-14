## MODIFIED Requirements

### Requirement: Routed subagent execution uses live registry and parent context

When a skill activation resolves through `metadata.subagent`, the runtime SHALL use the current validated subagent definition and the same immutable parent-context snapshot contract as explicit `spawn_agent` execution.

The runtime SHALL validate the skill metadata, target, and skill content before durable child acceptance. After acceptance, routed activation SHALL return the accepted run identity without waiting for child completion. The runtime SHALL deliver the result through the accepted parent run. Inline activation and deterministic failure without inline fallback SHALL remain unchanged.

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

#### Scenario: Routed activation returns durable acceptance

- **GIVEN** the skill metadata, target, content, and authority pass validation
- **WHEN** the runtime accepts the child run durably
- **THEN** routed activation returns the accepted run identity before child completion
- **AND** the later result remains correlated with the parent and original input

#### Scenario: Invalid routed activation starts no child

- **GIVEN** the skill metadata, target, content, or authority fails validation
- **WHEN** routed activation runs its pre-acceptance checks
- **THEN** it returns an explicit failure with remediation
- **AND** neither child nor inline execution starts
