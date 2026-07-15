## MODIFIED Requirements

### Requirement: Tool execution encapsulation

Tool execution SHALL be encapsulated in a composed `SessionToolExecutionPipeline`
whose unconditional production services are required constructor dependencies.
The session actor SHALL submit one cohesive batch command whose tool authority is
derived from the admitted `TurnContext`; callers SHALL NOT be able to supply a
second, conflicting authority source. Genuinely unavailable runtime capabilities,
including background-job dispatch, SHALL be represented explicitly while retaining
their existing behavior. The pipeline SHALL execute tool calls in parallel, track
sub-agent activity, and send completion or failure messages back to the actor.

The pipeline SHALL NOT itself bound or clamp tool-result size. Bounding to the
inline budget and spilling overflow remains centralized in
`DispatchingToolExecutor`, so the pipeline stores the result already bounded by
the dispatcher. `SessionTuning.MaxInlineToolResultChars` remains the session
content budget used for tools without a smaller per-tool override.

#### Scenario: Parallel tool execution

- **GIVEN** an admitted turn whose LLM response contains three tool calls
- **WHEN** the session submits its `SessionToolBatch`
- **THEN** all three tool calls execute in parallel with fresh call-local state
- **AND** results are collected and returned through the existing actor protocol

#### Scenario: Conflicting authority cannot be supplied

- **GIVEN** a session constructs a tool batch from an admitted `TurnContext`
- **WHEN** the batch derives its tool run scope
- **THEN** session, audience, boundary, channel, delivery, and interactive-approval authority come from that turn context
- **AND** the caller has no initializer or alternate constructor for replacing the derived authority

#### Scenario: Background manager is unavailable

- **GIVEN** a valid background-capable shell request and no registered background-job manager
- **WHEN** the batch executes
- **THEN** the request executes synchronously as it did before the composition refactor
- **AND** manager absence is not inferred from a nullable security dependency

#### Scenario: Tool execution timeout

- **GIVEN** tool execution is in progress
- **WHEN** the configured `ToolExecutionTimeout` elapses
- **THEN** the pipeline sends `ToolExecutionFailed` with a `TimeoutException`

#### Scenario: Oversized result already bounded by the dispatcher

- **GIVEN** a tool returns an oversized result
- **WHEN** it reaches the pipeline
- **THEN** the pipeline stores it as-is without re-clamping
