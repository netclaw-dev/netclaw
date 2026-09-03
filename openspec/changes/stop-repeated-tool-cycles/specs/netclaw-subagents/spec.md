## ADDED Requirements

### Requirement: Subagents stop exact repeated tool cycles

A subagent SHALL use the same action signature, iteration signature, cycle
periods, and six-iteration bound as a main session.

The first blocked batch SHALL return paired synthetic correction results. A
repeat of the blocked action SHALL force a final text response and mark the
subagent result as partial.

Subagent diagnostics SHALL follow the same payload exclusion rules as main
session diagnostics.

#### Scenario: Child period-one cycle blocks a third execution

- **GIVEN** a child action completed twice with equal outcomes
- **WHEN** the child model requests the action again
- **THEN** the child returns paired correction results without execution

#### Scenario: Child repeats a blocked action

- **GIVEN** a child received a correction for a blocked action
- **WHEN** it requests the same action again
- **THEN** the child makes a text-only model call
- **AND** its final outcome is partial

#### Scenario: Parent and child decisions match

- **GIVEN** equal synthetic histories and candidate batches
- **WHEN** a parent session and a subagent evaluate them
- **THEN** both produce the same cycle decision

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
- It SHALL inherit audience and boundary context from the launch invocation.

#### Scenario: Subagent completes with text response and findings

- **GIVEN** a `SubAgentDefinition` with a name, system prompt, and tool list
- **WHEN** the subagent receives a `RunSubAgent` message
- **THEN** the subagent executes its LLM and tool loop
- **AND** it returns one `SubAgentResult`
- **AND** the result may include structured findings for the parent session
- **AND** the subagent stops itself

#### Scenario: Subagent executes tool calls in a loop

- **GIVEN** the LLM returns `FunctionCallContent` tool calls
- **WHEN** the subagent processes the response
- **THEN** it executes allowed calls through `DispatchingToolExecutor`
- **AND** it sends tool results back to the LLM
- **AND** it continues until the LLM returns text or the cycle guard stops it

#### Scenario: Subagent hits maximum tool iterations

- **GIVEN** the final detector rollout removed the static child iteration limit
- **WHEN** a productive subagent exceeds the former iteration count
- **THEN** the subagent continues unless another runtime guard stops it
- **AND** iteration count alone does not force a final response

#### Scenario: Default subagent cannot write durable memory directly

- **GIVEN** a default subagent executes within a user-facing session
- **WHEN** it attempts to persist durable cross-session memory directly
- **THEN** the durable write path is unavailable or denied
- **AND** the subagent must return findings to the parent session

#### Scenario: Routed subagent does not inherit main identity prompt stack

- **GIVEN** a slash-invoked skill routes execution through `metadata.subagent`
- **WHEN** the routed subagent prompt is assembled
- **THEN** the main session identity prompt stack is not included by default

#### Scenario: Routed subagent does not auto-load repo AGENTS

- **GIVEN** a slash-invoked skill routes execution through `metadata.subagent`
- **WHEN** the routed subagent prompt is assembled
- **THEN** repo-local `AGENTS.md` is not loaded by default

#### Scenario: Routed subagent inherits launch audience

- **GIVEN** a routed subagent starts with the `team` audience
- **WHEN** the subagent executes tool calls
- **THEN** tool execution uses the `team` audience
- **AND** routed execution does not widen the audience
