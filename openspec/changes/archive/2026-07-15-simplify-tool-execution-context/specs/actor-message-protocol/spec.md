## ADDED Requirements

### Requirement: Execution-scope refactoring preserves external actor contracts

Run scopes, child scopes, activity trackers, and working-context deltas introduced for tool execution SHALL be framework-owned local actor messages. The refactoring SHALL NOT change existing persisted event shapes or MCP protocol payloads. Local messages SHALL remain serialization-safe where they cross actor boundaries.

#### Scenario: Existing MCP caller invokes a tool

- **GIVEN** an MCP client using the tool schema from before this change
- **WHEN** it invokes the tool after the internal execution refactoring
- **THEN** the request and response protocol remain compatible
- **AND** internal run-scope types are not exposed in the MCP schema

#### Scenario: Actor recovers persisted session state

- **GIVEN** session events persisted before this change
- **WHEN** the updated session actor recovers them
- **THEN** recovery succeeds without a data migration
- **AND** volatile run scopes and Git snapshots are reconstructed only for new work
