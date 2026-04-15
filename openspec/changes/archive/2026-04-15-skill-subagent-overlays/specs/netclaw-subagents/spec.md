# netclaw-subagents Delta Spec — skill-subagent-overlays

## MODIFIED Requirements

### Requirement: Subagent execution contract

The system SHALL run subagents as ephemeral actors (`SubAgentActor`) that
execute an autonomous LLM tool loop and return a single text result plus an
optional structured findings envelope. A subagent SHALL stop itself after
completing its task. Subagents SHALL NOT persist durable memory, stream direct
durable-memory writes, or participate in session pub/sub by default.

For subagent execution launched from skill metadata routing, the subagent SHALL
remain an isolated worker by default:

- It SHALL NOT inherit the main session identity prompt stack unless explicitly
  enabled by a future opt-in setting.
- It SHALL NOT auto-load repo-local `AGENTS.md` unless explicitly enabled by a
  future opt-in setting.
- It SHALL inherit audience/boundary context from the launching invocation.

#### Scenario: Subagent completes with text response and findings

- **GIVEN** a `SubAgentDefinition` with a name, system prompt, and tool list
- **WHEN** the subagent receives a `RunSubAgent` message
- **THEN** the subagent executes its LLM/tool loop and returns a `SubAgentResult`
- **AND** the result MAY include structured findings for the parent session to
  review
- **AND** stops itself

#### Scenario: Subagent executes tool calls in a loop

- **GIVEN** the LLM returns `FunctionCallContent` tool calls
- **WHEN** the subagent processes the response
- **THEN** it executes the tool calls via `DispatchingToolExecutor`
- **AND** sends tool results back to the LLM
- **AND** continues until the LLM returns a text response

#### Scenario: Subagent hits maximum tool iterations

- **GIVEN** the subagent has executed 10 tool iterations
- **WHEN** the LLM returns another tool call
- **THEN** the subagent forces a final LLM call with tools omitted
- **AND** returns the resulting text response

#### Scenario: Default subagent cannot write durable memory directly

- **GIVEN** a default subagent is executing within a user-facing session
- **WHEN** it attempts to persist durable cross-session memory directly
- **THEN** the durable write path is unavailable or denied to that subagent
- **AND** the subagent must return findings to the parent session instead

#### Scenario: Routed subagent does not inherit main identity prompt stack

- **GIVEN** a slash-invoked skill routes execution via `metadata.subagent`
- **WHEN** the routed subagent prompt is assembled
- **THEN** the main session identity prompt stack is not included by default

#### Scenario: Routed subagent does not auto-load repo AGENTS

- **GIVEN** a slash-invoked skill routes execution via `metadata.subagent`
- **WHEN** the routed subagent prompt is assembled
- **THEN** repo-local `AGENTS.md` is not auto-loaded by default

#### Scenario: Routed subagent inherits launch audience

- **GIVEN** a routed subagent activation launched from a parent invocation with
  audience `team`
- **WHEN** the subagent executes tool calls
- **THEN** tool execution context audience is `team`
- **AND** routed execution does not widen audience to a broader default

## ADDED Requirements

### Requirement: User-facing target validation for routed skill execution

Subagent targets selected by skill metadata routing SHALL be validated against
the subagent registry before execution. Routed skill execution SHALL only allow
known user-facing subagent targets. Unknown targets and internal-only targets
SHALL fail deterministically and SHALL NOT execute.

#### Scenario: Unknown subagent target fails deterministically

- **GIVEN** a slash-invoked skill with `metadata.subagent: missing-agent`
- **WHEN** routed execution is requested
- **THEN** execution fails with a deterministic unknown-target error
- **AND** no subagent actor is spawned

#### Scenario: Internal-only subagent target fails deterministically

- **GIVEN** a slash-invoked skill with `metadata.subagent` pointing to an
  internal-only subagent
- **WHEN** routed execution is requested
- **THEN** execution fails with a deterministic not-user-facing error
- **AND** no subagent actor is spawned
