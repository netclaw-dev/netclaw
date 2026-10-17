## MODIFIED Requirements

### Requirement: Subagent execution contract

The system SHALL run subagents as ephemeral workers that execute an autonomous tool loop and return a text result with optional structured findings. A subagent SHALL stop after its task. Subagents SHALL NOT participate in session pub/sub by default. Subagents SHALL NOT persist durable memory or write directly to durable memory.

The parent session SHALL durably accept a child run before it starts the worker. `spawn_agent` SHALL return the accepted run identity before the child completes. The parent SHALL retain run status, terminal results, approval correlation, and cancellation state across restart. Recovery SHALL NOT restart an uncertain child action.

For subagent execution launched from skill metadata routing, the subagent SHALL remain isolated by default. It SHALL NOT inherit the main identity prompt stack or load repository `AGENTS.md` by default. It SHALL inherit the audience and authority boundary of the launching invocation.

#### Scenario: Child acceptance returns before child completion

- **GIVEN** a valid child request whose durable acceptance succeeds
- **WHEN** the parent calls `spawn_agent`
- **THEN** the parent receives the accepted run identity before the child completes
- **AND** the child starts only after the acceptance persists

#### Scenario: Failed acceptance starts no child

- **GIVEN** a valid child request whose durable acceptance fails
- **WHEN** the parent calls `spawn_agent`
- **THEN** the call returns a failure
- **AND** the system starts no child work

#### Scenario: Subagent completes with text response and findings

- **GIVEN** a subagent has a valid assigned task
- **WHEN** it completes the task
- **THEN** the parent can receive the text result and optional structured findings
- **AND** the ephemeral worker stops

#### Scenario: Subagent executes tool calls in a loop

- **GIVEN** a subagent receives tool results for its task
- **WHEN** it requests further tools
- **THEN** the tool loop follows `turn-loop-governance`
- **AND** the system does not apply a separate child iteration ceiling

#### Scenario: Subagent hits maximum tool iterations

- **GIVEN** a subagent has completed 10 useful tool iterations
- **WHEN** it requests another distinct useful tool call
- **THEN** the system does not stop the child only because of the former iteration limit
- **AND** recurrence governance remains active

#### Scenario: Default subagent cannot write durable memory directly

- **GIVEN** a child is executing within a user-facing session
- **WHEN** it attempts to persist cross-session memory directly
- **THEN** the durable write path is unavailable or denied
- **AND** the child returns candidate findings to the parent

#### Scenario: Routed subagent does not inherit main identity prompt stack

- **GIVEN** a skill routes execution through subagent metadata
- **WHEN** the child prompt is assembled
- **THEN** the main identity stack and repository `AGENTS.md` are omitted by default

#### Scenario: Routed subagent does not auto-load repo AGENTS

- **GIVEN** a skill routes execution through subagent metadata
- **WHEN** the child prompt is assembled
- **THEN** repository `AGENTS.md` is not loaded by default

#### Scenario: Routed subagent inherits launch audience

- **GIVEN** a routed child starts from a Team-audience parent
- **WHEN** it executes tools
- **THEN** it retains the Team audience and authority boundary

### Requirement: Subagent observability events

The system SHALL emit structured `SubAgentOutput` events to session subscribers
when a subagent starts and completes. These events SHALL be filtered under the
`OutputFilter.ToolCalls` category. Tools that spawn subagents SHALL notify the
session via `ToolExecutionContext.OnSubAgentActivity`.

A child run completes after the turn that started it. The parent receives its result in a later
turn. A headless caller SHALL stay subscribed until that turn ends. See `netclaw-cli` and
`background-subagent-runs` for the wait rule and the delivered tool output.

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
- **AND** a subagent starts in turn 1 and completes after turn 1 ends
- **WHEN** the run proceeds until the parent has handled the child result
- **THEN** the CLI renders `[subagent:start] <name> (<N> tools)`
- **AND** renders `[subagent:waiting] <n> open: <runId>` while the run is open
- **AND** renders `[subagent:done] <name> (<status>, <duration>)` before it exits
- **AND** renders the delivered child result as `[tool:result] child_run_result → <result>`
- **AND** the process does not exit before the follow-up parent turn ends

#### Scenario: Headless CLI that exits at turn 1 loses the child result

- **GIVEN** a headless run whose turn 1 started a subagent
- **WHEN** the CLI exits when turn 1 completes
- **THEN** the run violates this requirement, because `[subagent:done]` and the child result never reach stdout

#### Scenario: Slack adapter suppresses subagent events

- **GIVEN** the Slack adapter subscribes to session output
- **WHEN** a subagent starts and completes
- **THEN** no subagent-specific messages are posted to Slack

## ADDED Requirements

### Requirement: The spawn_agent description tells the model not to wait

The `spawn_agent` tool description SHALL tell the model that the result arrives in a later turn. It SHALL tell the model not to wait and not to poll. The description SHALL hold these two sentences:

```text
Do not wait or poll for the child. End the turn. The result arrives in a later turn.
```

The model never needs `sleep`, `check_agent_run`, or an artifact read to wait for the result. The parent resumes when the result is ready. Changing this text changes model input, so the maintainer runs the eval suite for it.

#### Scenario: The description names the later turn

- **GIVEN** the `spawn_agent` tool definition
- **WHEN** a session builds the tool list for the model
- **THEN** the description holds "Do not wait or poll for the child."
- **AND** it holds "The result arrives in a later turn."

#### Scenario: The description does not teach a polling loop

- **GIVEN** the `spawn_agent` tool definition
- **WHEN** a reader checks its description
- **THEN** the description does not tell the model to call `check_agent_run` or `sleep` until the child ends
