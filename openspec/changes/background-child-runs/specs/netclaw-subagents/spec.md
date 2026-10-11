## MODIFIED Requirements

### Requirement: Subagent execution contract

The system SHALL run subagents as ephemeral actors (`SubAgentActor`) that execute an autonomous LLM tool loop.
A child SHALL return one terminal text result with an optional structured findings envelope through the background owner contract.
It SHALL close task dispatch and retain that result until the owner acknowledges its durable receipt or the owner terminates.
It SHALL then stop itself.
Subagents SHALL NOT persist durable memory, stream direct durable-memory writes, or participate in session pub/sub by default.

For subagent execution launched from skill metadata routing, the subagent SHALL remain an isolated worker by default:

- It SHALL receive the embedded operating core for its audience, followed by the operator's deployment `AGENTS.md`.
- It SHALL NOT inherit `SOUL.md` or `TOOLING.md`.
- A child with a non-Public audience SHALL receive available project instructions from the inherited project directory.
- A Public child SHALL NOT receive project-local instructions.
- It SHALL inherit audience/boundary context from the launch invocation. Prompt content SHALL NOT widen that authority.

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

#### Scenario: Routed subagent receives scoped operating guidance

- **GIVEN** a slash skill routes through `metadata.subagent` and a deployment playbook exists
- **WHEN** its isolated child prompt is assembled
- **THEN** the embedded operating core for its audience appears before the deployment `AGENTS.md`
- **AND** neither `SOUL.md` nor `TOOLING.md` is included

#### Scenario: Routed subagent receives inherited project instructions

- **GIVEN** a Personal or Team slash activation inherits a project directory with an `AGENTS.md` file
- **WHEN** its isolated child prompt is assembled
- **THEN** available project instructions appear after the operating guidance and before the child role prompt
- **AND** the project instructions come from the inherited project directory

#### Scenario: Public routed subagent excludes project instructions

- **GIVEN** a Public slash activation inherits a project directory with an `AGENTS.md` file
- **WHEN** its isolated child prompt is assembled
- **THEN** the stripped Public operating core and available deployment playbook remain present
- **AND** project-local instructions remain absent

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

### Requirement: Configurable subagent timeouts

The system SHALL use resolved subagent profile values for operation-health deadlines.
`SubAgentProfile.TimeoutSeconds` SHALL control inactivity between model deltas after substantive output starts.
`SubAgentProfile.PrefillTimeoutSeconds` SHALL override the shared wait for initial output when present.
When absent, it SHALL inherit `SubAgentConfig.PrefillTimeoutSeconds` from the `SubAgents` section of `netclaw.json`.
`SubAgentConfig.NoProgressTimeoutSeconds` SHALL bound time without substantive model output.
Content-free keepalives SHALL NOT refresh that no-progress deadline.

These values SHALL NOT define a wall-clock task-lifetime limit.
The file definition loader SHALL reject profile `timeoutSeconds` outside 5–600 seconds.
It SHALL reject profile `prefillTimeoutSeconds` outside 5–3600 seconds.
This change SHALL NOT migrate configuration fields or alter their current defaults.

#### Scenario: Custom timeout from configuration

- **GIVEN** a valid agent profile has `timeoutSeconds: 300`
- **WHEN** the spawner prepares that profile for the actual child actor
- **THEN** the child uses 300 seconds for model inactivity after substantive output starts
- **AND** healthy operations can continue beyond 300 seconds of total task duration

#### Scenario: Missing config section uses defaults

- **GIVEN** configuration omits the `SubAgents` section and the profile omits timeout overrides
- **WHEN** the runtime resolves the child operation-health values
- **THEN** it uses a 60-second profile inactivity value and a 1800-second initial-output value
- **AND** its no-progress value is 1200 seconds
- **AND** none of those values defines the child's total task duration

#### Scenario: Invalid profile timeout is rejected before execution

- **GIVEN** a file agent definition has `timeoutSeconds: -1`
- **WHEN** the definition loader validates that profile
- **THEN** it rejects that definition before a child can execute
- **AND** it does not replace the invalid value with a default

#### Scenario: Invalid timeout rejected by doctor

- **GIVEN** `netclaw.json` contains `"SubAgents": { "PrefillTimeoutSeconds": -1 }`
- **WHEN** the operator runs `netclaw doctor`
- **THEN** doctor reports a validation error for the timeout value

### Requirement: Subagent observability events

`LlmSessionActor` SHALL emit structured `SubAgentOutput` events to session subscribers for accepted child runs.
It SHALL emit `Started` only after the durable `Started` record commits.
It SHALL emit `Completed` only after the durable terminal receipt commits.
These events SHALL retain the `OutputFilter.ToolCalls` category and their existing payload fields.
Start-tool acceptance SHALL NOT substitute for the child terminal event.

The owner SHALL NOT require the completed start tool's activity callback to deliver later child events.
Existing headless CLI and channel render rules SHALL remain unchanged.

#### Scenario: Subagent start event emitted

- **GIVEN** an accepted child start within the session owner
- **WHEN** the owner commits `Started`
- **THEN** it emits one `SubAgentOutput` with `Phase = Started`
- **AND** that event includes the agent name and tool count
- **AND** subscribers receive it under `OutputFilter.ToolCalls`

#### Scenario: Subagent completion event emitted

- **GIVEN** a child finishes after its start tool returns acceptance
- **WHEN** the owner commits its terminal receipt
- **THEN** it emits one `SubAgentOutput` with `Phase = Completed`
- **AND** that event includes the existing success status, duration, and outcome fields

#### Scenario: Headless CLI renders subagent events

- **GIVEN** the headless CLI subscribes with `OutputFilter.Full`
- **WHEN** a subagent starts and completes
- **THEN** the CLI renders `[subagent:start] <name> (<N> tools)`
- **AND** it renders `[subagent:done] <name> (<status>, <duration>)`

#### Scenario: Slack adapter suppresses subagent events

- **GIVEN** the Slack adapter subscribes to session output
- **WHEN** a subagent starts and completes
- **THEN** no subagent-specific messages are posted to Slack

### Requirement: Sub-agent watchdog pauses during human approval

The sub-agent inactivity watchdog SHALL treat parent approval waits as intentional suspension. While one or more approval waits are active, watchdog timeout ticks SHALL NOT complete the sub-agent as inactive. When the last approval wait settles, the watchdog SHALL be re-baselined so future inactivity is still bounded.

#### Scenario: Slow approval does not trigger inactivity timeout
- **GIVEN** a sub-agent with an active approval wait
- **AND** the human approval decision takes longer than the sub-agent inactivity budget
- **WHEN** the approval eventually arrives
- **THEN** the sub-agent applies the approval outcome
- **AND** the sub-agent is not failed for inactivity during the wait

#### Scenario: Parent start completes before child approval
- **GIVEN** the parent start call returns durable child acceptance
- **AND** the child waits for human approval
- **WHEN** the parent performs independent authorized work
- **THEN** the child approval wait remains open and cancellable
- **AND** the completed parent start call has no watchdog dependency on that wait

#### Scenario: Parallel approval waits keep watchdog paused until all settle
- **GIVEN** a sub-agent tool batch with two approval-gated calls
- **WHEN** both calls are waiting for parent approval
- **THEN** the watchdog remains paused until both approval waits have settled
- **AND** the watchdog is re-armed only after the final wait completes
