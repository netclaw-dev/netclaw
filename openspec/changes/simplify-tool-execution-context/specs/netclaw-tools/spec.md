## ADDED Requirements

### Requirement: Tool invocation requires an admitted run scope

Every first-party tool invocation SHALL receive a non-null immutable invocation context created from an immutable run scope after audience admission. The runtime SHALL NOT expose a context-free production execution overload, an empty production execution context, or nullable authority dependencies. Mutable tool outputs SHALL be written through a separate per-invocation append-only sink, and approval attempt state SHALL remain outside the tool-visible context. Each invocation SHALL receive fresh output and approval state even when calls share one run scope.

#### Scenario: Parallel calls do not share call-local state

- **GIVEN** two tool calls in the same admitted turn
- **WHEN** the calls execute concurrently
- **THEN** both calls share the same immutable run authority
- **AND** outputs or approval mutations from one call are not visible to the other call

#### Scenario: Missing authority cannot reach dispatch

- **GIVEN** a caller has not constructed an admitted run scope
- **WHEN** it attempts to invoke a first-party tool
- **THEN** no context-free API permits dispatch
- **AND** the tool does not execute under default authority

### Requirement: Execution limits use validated semantic values

Timeouts, inline output budgets, and other scalar execution limits crossing the tool pipeline SHALL use validated semantic value objects. These value objects SHALL require explicit primitive access and SHALL NOT define implicit conversions to or from primitive types.

#### Scenario: Invalid limit is rejected at construction

- **GIVEN** an execution limit outside its permitted range
- **WHEN** the run scope or tool metadata is constructed
- **THEN** construction returns a validation failure before tool dispatch
- **AND** no default primitive value is substituted

### Requirement: Tool-enabled sessions require execution infrastructure

A tool-enabled session SHALL have authorization, approval, audit, logging, and dispatch infrastructure available before accepting a tool batch. Infrastructure that production constructs unconditionally SHALL be a required dependency rather than a nullable feature switch.

#### Scenario: Security dependency is unavailable

- **GIVEN** required authorization or approval infrastructure cannot be constructed
- **WHEN** the session attempts to enable tools
- **THEN** session initialization or batch execution fails visibly
- **AND** the missing dependency does not disable its check
