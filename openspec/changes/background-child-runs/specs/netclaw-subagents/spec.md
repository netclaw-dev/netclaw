## MODIFIED Requirements

### Requirement: Subagent execution contract

The system SHALL run subagents as ephemeral actors (`SubAgentActor`) that execute an autonomous LLM tool loop.
A child SHALL return one terminal text result with an optional structured findings envelope through the background owner contract.
It SHALL close task dispatch and retain that result until the owner acknowledges its durable receipt or the owner terminates.
It SHALL then stop itself.
Subagents SHALL NOT persist durable memory, stream direct durable-memory writes, or participate in session pub/sub by default.

For subagent execution launched from skill metadata routing, the subagent SHALL remain an isolated worker by default:

- It SHALL NOT inherit the main session identity prompt stack unless a future explicit setting enables it.
- It SHALL NOT auto-load repo-local `AGENTS.md` unless a future explicit setting enables it.
- It SHALL inherit audience/boundary context from the launch invocation.

The child SHALL use the shared exact recurrence, repeat exception, and feedback-round contracts in `turn-loop-governance`.
It SHALL retain its detector evidence for its actor-local task lifetime.
It SHALL NOT substitute success for an absent receipt or create a durable child-resumption contract.
All explicit and routed starts SHALL use `background-subagent-runs` acceptance, lifetime, and later-result rules.

#### Scenario: Subagent completes with text response and findings

- **GIVEN** a `SubAgentDefinition` with a name, system prompt, and tool list
- **WHEN** the child finishes its task
- **THEN** it returns a `SubAgentResult` to its owner
- **AND** optional findings remain available for parent review
- **AND** the child stops after durable receipt acknowledgement or owner termination

#### Scenario: Subagent executes tool calls in a loop

- **GIVEN** the model returns `FunctionCallContent` tool calls
- **WHEN** the child processes that response
- **THEN** eligible calls use the normal dispatch and authorization contracts
- **AND** their paired results return to the model
- **AND** the loop continues until text completion or an explicit runtime terminal decision

#### Scenario: Subagent hits maximum tool iterations

- **GIVEN** a child exceeds the former tool iteration limit through useful work
- **WHEN** it requests another eligible call
- **THEN** no removed iteration ceiling forces a final model request
- **AND** operation health and recurrence rules remain effective

#### Scenario: Useful child work exceeds the former ceiling

- **GIVEN** a child task needs more than 30 useful feedback rounds
- **WHEN** a deterministic provider drives that task through real dispatch
- **THEN** no count-based stop ends the task
- **AND** independent artifact checks prove its required result

#### Scenario: Child recurrence settles without another model response

- **GIVEN** the shared recurrence contract requires terminal settlement
- **WHEN** the child settles that run
- **THEN** it returns `Partial` with `ToolCycleStopped` and confirmed activity
- **AND** it requests no final model response
- **AND** the owner persists and delivers that terminal result through the background contract

#### Scenario: Default subagent cannot write durable memory directly

- **GIVEN** a default child executes within a user-facing session
- **WHEN** it attempts a direct durable cross-session memory write
- **THEN** that path remains unavailable or denied
- **AND** the child returns findings for parent policy review instead

#### Scenario: Routed subagent does not inherit main identity prompt stack

- **GIVEN** a slash skill routes through `metadata.subagent`
- **WHEN** its isolated child prompt is assembled
- **THEN** the main session identity stack remains absent by default

#### Scenario: Routed subagent does not auto-load repo AGENTS

- **GIVEN** a slash skill routes through `metadata.subagent`
- **WHEN** its isolated child prompt is assembled
- **THEN** repo-local `AGENTS.md` remains absent by default

#### Scenario: Routed subagent inherits launch audience

- **GIVEN** a routed launch has audience `Team`
- **WHEN** the child executes a task tool
- **THEN** that tool uses inherited `Team` authority
- **AND** background lifetime does not widen its audience

### Requirement: Subagent timeout enforcement

The system SHALL enforce child operation health checks, not a wall-clock task-lifetime timeout.
The checks SHALL distinguish first substantive output, inter-delta inactivity, and content-free keepalives.
A child stall SHALL return an explicit failed terminal result through the background owner contract.
An authorized human approval wait SHALL remain independent of operation inactivity and remain cancellable.
Healthy task duration alone SHALL NOT cause terminal timeout.

#### Scenario: Subagent times out

- **GIVEN** a child provider operation has an applicable inactivity deadline
- **WHEN** the operation exceeds that deadline without the required progress
- **THEN** the child returns a failed terminal result with the corresponding timeout reason
- **AND** the owner records it and the child stops after receipt acknowledgement

#### Scenario: LLM call failure returns failure result

- **GIVEN** a child model call throws an exception
- **WHEN** the child processes that failure
- **THEN** it returns an explicit failed result
- **AND** background delivery preserves the failure rather than success

#### Scenario: Healthy child outlives one operation deadline

- **GIVEN** a child repeatedly completes healthy model and tool operations
- **WHEN** total task duration exceeds any individual operation deadline
- **THEN** task age alone does not stop the child
- **AND** individual stalled operations still trigger their health checks

#### Scenario: A live approval wait does not become a stall

- **GIVEN** a child waits on its run-owned approval prompt
- **WHEN** operation inactivity would otherwise expire
- **THEN** the legitimate approval wait remains open
- **AND** explicit run cancellation can still settle it
