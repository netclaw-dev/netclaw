## ADDED Requirements

### Requirement: Tool execution is a streaming contract

Tool execution SHALL be expressed as a stream: an invocation yields an ordered
sequence of `ToolCallUpdate` items — zero or more non-terminal *activity* items
followed by exactly one terminal *completion* item. The completion item SHALL
carry the tool result text. Invocation side effects that are not part of the
tools-abstractions assembly contract, such as file attachments and sub-agent
outputs, SHALL continue to be collected through the tool execution context and
returned by the session tool-execution pipeline with the terminal result.

`INetclawTool` SHALL expose the streaming method as a default interface method
whose default implementation yields a single completion item wrapping the tool's
existing non-streaming result. A tool that does not override the method SHALL
therefore behave identically to its current non-streaming behavior.

#### Scenario: Non-streaming tool yields a single completion item

- **GIVEN** a tool that does not override the streaming method
- **WHEN** it is invoked
- **THEN** exactly one terminal completion item is produced
- **AND** it carries the same result the non-streaming execution would have returned

#### Scenario: Streaming tool emits activity then completion

- **GIVEN** a tool that overrides the streaming method (e.g. `shell_execute`)
- **WHEN** it is invoked and runs over time
- **THEN** it emits one or more activity items while working
- **AND** it finishes with exactly one terminal completion item

### Requirement: Per-call liveness by tool class

How a tool call is bounded SHALL depend on the tool's declared liveness class.

An **opaque** tool (the default) SHALL be bounded by one wall-clock budget applied
to the whole call. Streamed output SHALL NOT extend that budget — a chatty stream
cannot keep an opaque tool alive past its budget. When the budget elapses the
watchdog SHALL cancel the call, and the call SHALL yield a terminal error result
identifying the tool and the timeout.

A **self-monitoring** tool (e.g. `spawn_agent`) owns its liveness end to end and
SHALL NOT be supervised by the parent at all. The tool-execution layer SHALL drain
such a call to its terminal completion item with no parent watchdog; the call is
bounded only by the tool's own internal watchdog (which SHALL produce a terminal
result on stall) or by caller (turn/user) cancellation. A self-monitoring tool's
declared class MUST match its resolved liveness mode in both directions; a mismatch
SHALL fail loudly at startup rather than leave a tool unsupervised at runtime.

Budgets and draining SHALL be per call. Parallel tool calls SHALL be monitored
independently — activity on one call SHALL NOT extend the budget of another.

#### Scenario: A chatty opaque stream still times out at its budget

- **GIVEN** an opaque streaming tool call emitting steady output
- **WHEN** its wall-clock budget elapses
- **THEN** the watchdog cancels the call
- **AND** the call yields a terminal error naming the tool and the timeout

#### Scenario: A self-monitoring call is not bounded by a parent watchdog

- **GIVEN** a self-monitoring tool call (e.g. `spawn_agent`)
- **WHEN** it runs longer than any prior parent budget, including a long quiet window
- **THEN** the parent does not cancel it
- **AND** it ends only on its own terminal item or caller cancellation

#### Scenario: A stalled self-monitoring tool is terminated by its own watchdog

- **GIVEN** a self-monitoring tool whose work stalls with no progress
- **WHEN** its own internal watchdog fires
- **THEN** the tool yields a terminal error result naming the stall reason
- **AND** the parent receives that terminal item without having supervised the call

#### Scenario: A healthy opaque call does not mask a stalled sibling

- **GIVEN** two opaque tool calls executing in parallel
- **AND** one is emitting output steadily
- **AND** the other has gone silent past its wall-clock budget
- **THEN** the silent call is timed out independently
- **AND** the healthy call continues unaffected

#### Scenario: A tool that resolves self-monitoring without declaring it is rejected at startup

- **GIVEN** a tool whose resolved liveness mode is self-monitoring
- **AND** it does not declare `SelfMonitoring` via its `[NetclawTool(Liveness=…)]` attribute
- **THEN** startup validation fails loudly
- **AND** the tool is never drained unsupervised

### Requirement: Only the terminal result enters the conversation

Only the terminal completion item's result SHALL be appended to the conversation
as the tool-result message, clamped to the configured maximum inline tool-result
size. Activity items SHALL NOT be accumulated into the conversation or the LLM
context; they serve only the per-call watchdog and an optional live output relay.

#### Scenario: Streamed intermediate output does not reach the LLM

- **GIVEN** a streaming tool that emits many activity items with output chunks
- **WHEN** the call completes
- **THEN** the LLM receives exactly one tool-result message for the call
- **AND** that message contains only the terminal result, clamped as today
- **AND** the intermediate activity chunks are absent from the conversation

### Requirement: Per-tool failures are isolated from the batch

A tool call that fails or times out SHALL yield a terminal error result keyed to
its own tool-call id. It SHALL NOT abort, discard, or fault sibling tool calls
executing in the same batch. Every tool call in a batch SHALL produce exactly one
tool-result message — success or error.

#### Scenario: One tool fails, siblings still return

- **GIVEN** a batch of tool calls executed in parallel
- **WHEN** one call fails or times out
- **THEN** that call produces a tool-result message containing its error
- **AND** every other call produces its normal tool-result message
- **AND** the turn continues with all tool-result messages delivered to the LLM
