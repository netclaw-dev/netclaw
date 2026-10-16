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

#### Scenario: Subagent fails a cut-off reply

- **GIVEN** the model reply has finish reason `length` and no tool call
- **WHEN** the subagent handles the reply
- **THEN** the subagent returns `SubAgentResult` with `Success = false`
- **AND** the outcome is Failed with reason `output_truncated`
- **AND** the subagent makes no retry
- **AND** the output holds a preview of at most 300 characters
- **AND** the preview passes the redactor before the cut
- **AND** the cut does not split a text element, such as a surrogate pair
- **AND** the subagent stops itself

#### Scenario: Secret at the cut point is not partly shown

- **GIVEN** a cut-off reply holds a secret that spans character 300
- **WHEN** the subagent builds the preview
- **THEN** the preview does not hold a partial value of the secret

#### Scenario: Complete reply is still a success

- **GIVEN** the model reply has finish reason `stop` and text
- **WHEN** the subagent handles the reply
- **THEN** the subagent returns `SubAgentResult` with `Success = true`

#### Scenario: Length reply with a tool call runs the tool

- **GIVEN** the model reply has finish reason `length` and a complete tool call
- **WHEN** the subagent handles the reply
- **THEN** the subagent runs the tool and continues the loop
- **AND** the run does not fail with reason `output_truncated`

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

#### Scenario: Thinking-only model call stops at the thinking cap

The system SHALL stop a subagent when one model call streams only thinking for
three times the no-progress timeout. A thinking update resets the no-progress
deadline but not the cap. Only an answer update restarts the cap. An answer
update is non-empty text, a tool call, or a finish reason. Empty or
whitespace-only text counts as thinking.

- **GIVEN** a subagent model call streams thinking at short intervals
- **AND** the call streams no answer update
- **WHEN** three times the no-progress timeout elapse
- **THEN** the subagent returns `SubAgentResult` with `Success = false`
- **AND** the outcome is Failed with reason `thinking_only_timeout`
- **AND** the subagent stops itself

#### Scenario: Thinking before an answer is not stopped

- **GIVEN** a subagent model call has streamed thinking for longer than the
  no-progress timeout but shorter than the cap
- **WHEN** a non-empty text update arrives
- **THEN** the subagent continues
- **AND** the cap restarts from that answer update

#### Scenario: Empty text does not restart the cap

- **GIVEN** a subagent model call streams thinking and empty text in turn
- **WHEN** three times the no-progress timeout elapse
- **THEN** the outcome is Failed with reason `thinking_only_timeout`
