## ADDED Requirements

### Requirement: Turn operation ownership isolation

The session system SHALL correlate each asynchronous LLM request and tool-execution batch to the active turn operation. Late completions, failures, deltas, or timeout callbacks from superseded operations SHALL be ignored and SHALL NOT mutate history, outputs, or active-turn state.

#### Scenario: Late LLM completion ignored after timeout
- **GIVEN** an LLM operation timed out and the session already advanced to a new terminal or recovery state
- **WHEN** the timed-out operation later reports text, tool calls, or completion
- **THEN** the session ignores the late message
- **AND** it does not append assistant content, emit user-visible output, or reset the active turn state

#### Scenario: Late tool completion ignored after retry
- **GIVEN** a tool-execution batch was replaced by a newer turn operation
- **WHEN** a completion or failure arrives from the older batch
- **THEN** the session ignores that stale tool result
- **AND** only the active batch may influence the current turn

### Requirement: Durable failed-turn and buffered replay recovery

Once a user message is accepted into a turn, the session system SHALL durably record terminal failure outcomes and any accepted buffered follow-up inputs needed for replay. Recovery after actor or daemon restart SHALL rebuild the pending follow-up queue in original order and SHALL preserve the last recorded terminal outcome for the interrupted turn.

#### Scenario: Failed accepted turn survives restart
- **GIVEN** a user turn was accepted and then failed due to timeout, provider failure, or tool failure
- **WHEN** the session recovers after restart
- **THEN** the failed turn outcome remains represented in recovered state
- **AND** the session does not resurrect the failed turn as if it were still processing

#### Scenario: Buffered follow-up inputs replay once after restart
- **GIVEN** one or more follow-up user messages were accepted while the current turn was processing
- **WHEN** the actor restarts before draining the buffered queue
- **THEN** the recovered session replays those buffered inputs once in original arrival order
- **AND** no accepted follow-up input is silently dropped or duplicated

### Requirement: Absolute wall-clock turn budget

The session system SHALL enforce a cumulative wall-clock budget for each user turn in addition to per-operation watchdogs. When the budget is exceeded, the session SHALL stop further LLM and tool work, record a timed-out terminal outcome, and advance recovery or buffered follow-up processing.

#### Scenario: Long multi-iteration turn exceeds wall-clock budget
- **GIVEN** a turn spans multiple LLM calls and tool-execution batches
- **WHEN** the total elapsed turn time exceeds the configured budget
- **THEN** the session aborts further work for that turn
- **AND** records a timeout outcome before advancing to any buffered follow-up input

### Requirement: Degraded completion after tool-budget exhaustion

The session system SHALL treat tool-iteration exhaustion as a degraded completion path rather than a silent or generic provider failure. When the tool budget is exhausted, the session SHALL first force a no-tools completion that prefers a best-effort answer or one focused clarifying question. If the model still attempts more tool work, the session SHALL emit a deterministic degraded user-visible terminal message.

#### Scenario: Exhausted tool budget yields best-effort completion
- **GIVEN** the current turn exhausted its configured tool-iteration budget
- **WHEN** the session forces a no-tools completion
- **THEN** the turn ends with a user-visible answer or one focused clarifying question
- **AND** the turn does not remain empty while waiting for more tools

#### Scenario: Noncompliant no-tools response yields deterministic degraded message
- **GIVEN** the tool budget is exhausted and the model still emits tool calls while tools are disallowed
- **WHEN** the session cannot obtain a compliant no-tools completion
- **THEN** the session emits a deterministic degraded terminal message
- **AND** it does not classify the turn as a generic provider failure solely because the tool budget was exhausted
