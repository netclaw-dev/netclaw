# netclaw-subagents Specification

## Purpose

Define subagent execution contract, timeout enforcement, observability events,
model role conventions, and context layer awareness for ephemeral autonomous
LLM actors.

## Requirements

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

### Requirement: Subagent findings handoff to owning session

When a subagent discovers information that may deserve durable memory, it SHALL
return that information as a structured findings envelope to the owning
session. The owning session SHALL evaluate policy, convert accepted findings
into checkpoints, and remain the default durable-memory owner.

#### Scenario: Parent session accepts findings for checkpoint review

- **GIVEN** a subagent returns findings that include stable project information
- **WHEN** the parent session evaluates the subagent result
- **THEN** the parent session converts the accepted findings into a durable
  memory checkpoint
- **AND** background curation proceeds under the parent session's policy scope

#### Scenario: Parent session rejects findings on policy grounds

- **GIVEN** a subagent returns findings whose domain or sensitivity violates the
  parent session's durable-memory policy
- **WHEN** the parent session evaluates the findings envelope
- **THEN** the findings are dropped or kept transient only
- **AND** no durable memory write occurs

### Requirement: Subagent timeout enforcement

The system SHALL enforce a wall-clock timeout on subagent execution. When the
timeout fires, the subagent SHALL return a failure result and stop itself.

#### Scenario: Subagent times out

- **GIVEN** a `RunSubAgent` message with a `Timeout` of 30 seconds
- **WHEN** 30 seconds elapse without completion
- **THEN** the subagent returns `SubAgentResult` with `Success = false`
- **AND** the output contains "timed out"
- **AND** the subagent stops itself

#### Scenario: LLM call failure returns failure result

- **GIVEN** the LLM throws an exception during a subagent call
- **WHEN** the subagent processes the error
- **THEN** it returns `SubAgentResult` with `Success = false`
- **AND** the output contains the error message
- **AND** the subagent stops itself

### Requirement: Configurable subagent timeouts

The system SHALL read subagent timeout values from the `SubAgents` section of
`netclaw.json`. When the section is absent, the system SHALL use built-in
defaults that match the current hardcoded values (180s for store, 30s for
search, 60s general default). Timeout values MUST be positive integers
between 5 and 600 seconds.

#### Scenario: Custom timeout from configuration

- **GIVEN** `netclaw.json` contains `"SubAgents": { "StoreMemoryTimeoutSeconds": 300 }`
- **WHEN** the `store_memory` tool spawns a subagent
- **THEN** the subagent uses a 300-second timeout

#### Scenario: Missing config section uses defaults

- **GIVEN** `netclaw.json` does not contain a `SubAgents` section
- **WHEN** the `store_memory` tool spawns a subagent
- **THEN** the subagent uses the default 180-second timeout

#### Scenario: Invalid timeout rejected by doctor

- **GIVEN** `netclaw.json` contains `"SubAgents": { "DefaultTimeoutSeconds": -1 }`
- **WHEN** the operator runs `netclaw doctor`
- **THEN** doctor reports a validation error for the timeout value

### Requirement: Subagent observability events

The system SHALL emit structured `SubAgentOutput` events to session subscribers
when a subagent starts and completes. These events SHALL be filtered under the
`OutputFilter.ToolCalls` category. Tools that spawn subagents SHALL notify the
session via `ToolExecutionContext.OnSubAgentActivity`.

#### Scenario: Subagent start event emitted

- **GIVEN** a tool spawns a subagent within a session's tool execution pipeline
- **WHEN** the subagent begins execution
- **THEN** a `SubAgentOutput` event with `Phase = Started` is emitted
- **AND** the event includes the agent name and tool count
- **AND** the event is delivered to subscribers with `ToolCalls` in their filter

#### Scenario: Subagent completion event emitted

- **GIVEN** a subagent completes (success or failure)
- **WHEN** the result is received by the calling tool
- **THEN** a `SubAgentOutput` event with `Phase = Completed` is emitted
- **AND** the event includes success status and duration

#### Scenario: Headless CLI renders subagent events

- **GIVEN** the headless CLI subscribes with `OutputFilter.Full`
- **WHEN** a subagent starts and completes
- **THEN** the CLI renders `[subagent:start] <name> (<N> tools)`
- **AND** renders `[subagent:done] <name> (<status>, <duration>)`

#### Scenario: Slack adapter suppresses subagent events

- **GIVEN** the Slack adapter subscribes to session output
- **WHEN** a subagent starts and completes
- **THEN** no subagent-specific messages are posted to Slack

### Requirement: Subagent model role convention

Subagents SHALL use `ModelRole.Compaction` by default. This routes to the
configured compaction model (cheaper/faster) rather than the main model. The
`SubAgentDefinition.ModelRole` property SHALL allow override per-definition.

#### Scenario: Subagent uses compaction model

- **GIVEN** `Models.Compaction` is configured in `netclaw.json`
- **WHEN** a subagent is spawned with default `ModelRole`
- **THEN** the subagent uses the compaction model

#### Scenario: Compaction model falls back to main

- **GIVEN** `Models.Compaction` is not configured
- **WHEN** a subagent is spawned
- **THEN** the subagent uses the main model as fallback

### Requirement: Context layer subagent awareness

The `MemorizerConnected` context layer SHALL inform the frontline model that
`store_memory` delegates to a curation subagent. The text SHALL set
expectations about latency (10–30 seconds for `store_memory`) so the model does
not retry or apologize for tool call duration. `find_memories`, `get_memories`,
and `update_memory` are direct MCP pass-throughs and do not use subagents.

#### Scenario: Context layer mentions subagent delegation

- **GIVEN** the memory provider is `memorizer` and Memorizer is connected
- **WHEN** the context layer is assembled for a session
- **THEN** the context includes a note about `store_memory` subagent delegation
- **AND** mentions expected latency of 10–30 seconds for store operations
