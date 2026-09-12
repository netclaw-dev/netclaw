## ADDED Requirements

### Requirement: Tool execution telemetry carries authorization correlation

Every local and MCP tool execution SHALL carry the call's PII-free authorization-attempt identifier into structured start and terminal-result telemetry. The identifier SHALL be internal execution metadata and SHALL NOT appear in model-visible tool definitions, tool arguments, or tool results. A tool implementation SHALL NOT be able to set approval state or use the identifier to change authorization.

#### Scenario: Local tool execution is correlated

- **WHEN** an authorized local tool starts and completes
- **THEN** its structured start and terminal-result events use the same `AuthorizationAttemptId` as its authorization policy events

#### Scenario: MCP tool execution is correlated

- **WHEN** an authorized MCP tool starts and completes
- **THEN** its structured start and terminal-result events use the same `AuthorizationAttemptId` as its authorization policy events
- **AND** no MCP request or response field is added solely for this identifier

#### Scenario: Model contract is unchanged

- **WHEN** the runtime builds tool schemas or model-visible tool results
- **THEN** the authorization-attempt identifier is absent from those contracts

#### Scenario: Tool cannot grant itself access

- **GIVEN** a tool implementation executes with its normal invocation context
- **WHEN** it runs
- **THEN** it cannot replace the authorization-attempt identifier or use it to seed an approval
