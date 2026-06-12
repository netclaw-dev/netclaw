## MODIFIED Requirements

### Requirement: Tool execution encapsulation

Tool execution SHALL be encapsulated in a `SessionToolExecutionPipeline` static
utility class. The pipeline SHALL execute tool calls in parallel, track sub-agent
activity, and send `ToolExecutionCompleted` or `ToolExecutionFailed` back to the
actor. The pipeline SHALL NOT itself bound or clamp tool-result size: bounding the
result to the inline budget and spilling the overflow is done once, centrally, by
`DispatchingToolExecutor` (per the `bounded-tool-output` capability), so the
pipeline stores the result the dispatcher already bounded. `SessionTuning.MaxInlineToolResultChars`
is the session **content** budget the dispatcher uses for tools without a smaller
per-tool override.

#### Scenario: Parallel tool execution

- **GIVEN** an LLM response contains 3 tool calls
- **WHEN** `SessionToolExecutionPipeline.ExecuteToolsAsync()` runs
- **THEN** all 3 tool calls execute in parallel
- **AND** results are collected and sent as a single `ToolExecutionCompleted`

#### Scenario: Tool execution timeout

- **GIVEN** tool execution is in progress
- **WHEN** the configured `ToolExecutionTimeout` elapses
- **THEN** the pipeline sends `ToolExecutionFailed` with a `TimeoutException`

#### Scenario: Oversized result already bounded by the dispatcher

- **GIVEN** a tool returns an oversized result
- **WHEN** it reaches the pipeline
- **THEN** the pipeline stores it as-is (already windowed + spilled by
  `DispatchingToolExecutor`) without re-clamping
