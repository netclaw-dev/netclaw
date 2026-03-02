## ADDED Requirements

### Requirement: Subagent execution contract

The system SHALL run subagents as ephemeral actors (`SubAgentActor`) that
execute an autonomous LLM tool loop and return a single text result. A
subagent SHALL stop itself after completing its task. Subagents SHALL NOT
persist conversation state, stream output, or participate in session pub/sub.

#### Scenario: Subagent completes with text response

- **GIVEN** a `SubAgentDefinition` with a name, system prompt, and tool list
- **WHEN** the subagent receives a `RunSubAgent` message
- **THEN** the subagent executes an LLM call with its tool subset
- **AND** returns a `SubAgentResult` with `Success = true` and the final text
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
`store_memory` and `search_memories` delegate to curation subagents. The text
SHALL set expectations about latency (10–30 seconds) so the model does not
retry or apologize for tool call duration.

#### Scenario: Context layer mentions subagent delegation

- **GIVEN** the memory provider is `memorizer` and Memorizer is connected
- **WHEN** the context layer is assembled for a session
- **THEN** the context includes a note about subagent delegation
- **AND** mentions expected latency of 10–30 seconds
