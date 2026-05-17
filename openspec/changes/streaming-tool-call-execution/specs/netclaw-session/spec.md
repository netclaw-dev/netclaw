## ADDED Requirements

### Requirement: The processing watchdog covers LLM calls and compaction only

The session processing watchdog SHALL govern only LLM streaming calls and
compaction. It SHALL NOT be armed for tool execution. Tool-call liveness SHALL be
the responsibility of the per-call watchdog in the tool-execution layer, owned by
neither the session actor nor the sub-agent actor.

#### Scenario: Tool execution does not arm the processing watchdog

- **GIVEN** the session dispatches a batch of tool calls
- **THEN** the processing watchdog is not armed for a tool-execution operation
- **AND** each tool call is monitored only by its own per-call watchdog

#### Scenario: A long tool call does not trip a session-level timeout

- **GIVEN** a tool call that runs longer than the former tool-execution budget
- **AND** it is emitting activity within its per-call inactivity budget
- **THEN** no session-level watchdog fails the turn
- **AND** the tool call runs to completion

### Requirement: A tool batch fails the turn only on infrastructure failure

A batch of tool calls SHALL complete and deliver every tool-result message
whenever each tool call resolves to a result — success or per-call error. The
turn SHALL be failed wholesale only when the tool-execution pipeline itself fails
for reasons outside any individual tool call.

#### Scenario: A per-call timeout does not fail the turn

- **GIVEN** a batch of tool calls
- **WHEN** one call times out under its per-call watchdog
- **THEN** that call returns a timeout error result
- **AND** the batch completes and delivers all tool-result messages
- **AND** the turn is not failed wholesale
