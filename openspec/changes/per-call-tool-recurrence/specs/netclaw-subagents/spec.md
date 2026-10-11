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


The child SHALL use the shared exact recurrence, repeat exception, and feedback-round contracts in `turn-loop-governance`.
The child SHALL retain its detector evidence for its actor-local task lifetime.
It SHALL NOT substitute success for an absent receipt or create a durable child-resumption contract.

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
- **AND** continues until a text response or an explicit runtime terminal decision

#### Scenario: Subagent hits maximum tool iterations

- **GIVEN** a child completed more than the former tool iteration limit with useful work
- **WHEN** the model requests another eligible tool call
- **THEN** the removed iteration ceiling does not force a final model request
- **AND** the child continues under the shared recurrence and operation contracts

#### Scenario: Useful child work exceeds the former ceiling

- **GIVEN** a child task requires more than 30 useful tool rounds
- **WHEN** a deterministic provider drives that task through the real child loop
- **THEN** no tool count or iteration count stops the task
- **AND** independent artifact checks prove the required result

#### Scenario: Child recurrence settles without another model response

- **GIVEN** two equal completed call outcomes and a subsequent paired correction
- **WHEN** the child requests the same prohibited identity again
- **THEN** the child returns a Partial result with `ToolCycleStopped`
- **AND** it retains confirmed file activity and accurate partial evidence
- **AND** it requests no final model response

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
