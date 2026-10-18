## ADDED Requirements

### Requirement: Truncated subagent reply fails the run

The system SHALL NOT complete a subagent run as success when the model reply has
finish reason `length` and no tool call. The `SubAgentActor` owns this decision.
The run SHALL end with outcome Failed and reason `output_truncated`. The
subagent SHALL NOT retry the call. The failure text SHALL hold a preview of the
cut-off text of at most 300 characters. The preview SHALL pass the secret
redactor before line breaks are escaped and before the cut. The cut SHALL NOT
split a text element. A reply with finish reason `length` and a tool call SHALL
run its tools.

#### Scenario: Subagent fails a cut-off reply

- **GIVEN** the model reply has finish reason `length` and no tool call
- **WHEN** the subagent handles the reply
- **THEN** the run ends with outcome Failed and reason `output_truncated`
- **AND** the subagent makes no retry
- **AND** the failure text holds a preview of at most 300 characters
- **AND** the cut does not split a text element, such as a surrogate pair
- **AND** the subagent stops itself

#### Scenario: Secret in the cut-off text is not shown

- **GIVEN** a cut-off reply holds a secret that spans character 300, or a
  secret that starts a line
- **WHEN** the subagent builds the preview
- **THEN** the preview does not hold the secret or a partial value of it

#### Scenario: Complete reply is still a success

- **GIVEN** the model reply has finish reason `stop` and text
- **WHEN** the subagent handles the reply
- **THEN** the run ends with outcome Completed

#### Scenario: Length reply with a tool call runs the tool

- **GIVEN** the model reply has finish reason `length` and a complete tool call
- **WHEN** the subagent handles the reply
- **THEN** the subagent runs the tool and continues the loop
- **AND** the run does not fail with reason `output_truncated`

### Requirement: Thinking-only subagent model call is capped

The system SHALL stop a subagent when one model call streams only thinking for
three times `SubAgents.NoProgressTimeoutSeconds`. The shared
`ProcessingWatchdog` owns the cap, as in the session. A thinking update SHALL
reset the no-progress deadline but SHALL NOT restart the cap. Only an answer
update SHALL restart the cap. Empty or whitespace-only text counts as thinking.
The run SHALL end with outcome Failed and reason `thinking_only_timeout`. Like
the no-progress deadline, the cap gets no approval grace.

#### Scenario: Thinking-only model call stops at the cap

- **GIVEN** a subagent model call streams thinking at short intervals
- **AND** the call streams no answer update
- **WHEN** three times the no-progress timeout elapse
- **THEN** the run ends with outcome Failed and reason `thinking_only_timeout`
- **AND** the subagent stops itself

#### Scenario: Thinking before an answer is not stopped

- **GIVEN** a subagent model call has streamed thinking for longer than the
  no-progress timeout but shorter than the cap
- **WHEN** a non-empty text update arrives
- **THEN** the subagent continues
- **AND** the cap restarts from that answer update

#### Scenario: Empty text does not restart the cap

- **GIVEN** a subagent model call streams thinking and empty text in turn
- **WHEN** three times the no-progress timeout elapse
- **THEN** the run ends with outcome Failed and reason `thinking_only_timeout`
