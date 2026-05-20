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

### Requirement: Per-call two-phase inactivity watchdog

Each tool call SHALL be monitored by its own two-phase inactivity watchdog in the
tool-execution layer: a first-item budget bounding the time to the first
`ToolCallUpdate`, and an inter-item budget that resets on every subsequent item.
When a budget elapses the watchdog SHALL cancel that call, and the call SHALL
yield a terminal error result identifying the tool and the timeout.

The watchdog SHALL be per call. Parallel tool calls SHALL be monitored
independently — activity on one call SHALL NOT extend the budget of another.

#### Scenario: Stalled stream trips the inter-item budget

- **GIVEN** a streaming tool call that has emitted at least one activity item
- **WHEN** no further item arrives within the inter-item budget
- **THEN** the watchdog cancels the call
- **AND** the call yields a terminal error naming the tool and the timeout

#### Scenario: Slow first item trips the first-item budget

- **GIVEN** a tool call that has emitted no items yet
- **WHEN** the first-item budget elapses
- **THEN** the watchdog cancels the call
- **AND** the call yields a terminal error naming the tool and the timeout

#### Scenario: A healthy call does not mask a stalled sibling

- **GIVEN** two tool calls executing in parallel
- **AND** one is emitting activity items steadily
- **AND** the other has gone silent past its inter-item budget
- **THEN** the silent call is timed out independently
- **AND** the healthy call continues unaffected

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
