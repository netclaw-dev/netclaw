## ADDED Requirements

### Requirement: Truncated model reply fails the turn

The system SHALL NOT complete a turn as success when the model reply has finish
reason `length` and no tool call. The turn SHALL end with an error that says the
reply was truncated. The error category SHALL be `ErrorCategory.ModelBehavior`,
because the provider worked and the model used all of its output budget. The
session SHALL NOT post the cut-off text as the final reply and SHALL NOT add it
to the conversation history. A reply with finish reason `length` and a tool call
SHALL run its tools. A thinking-only or empty reply with finish reason `length`
SHALL follow the existing empty-response retry path.

#### Scenario: Cut-off text reply ends the turn with a model error

- **GIVEN** the model streams text and ends with finish reason `length`
- **AND** the reply has no tool call
- **WHEN** the session handles the reply
- **THEN** the turn ends with an error that says the reply was truncated
- **AND** the error category is `ErrorCategory.ModelBehavior`
- **AND** the final reply output does not contain the cut-off text
- **AND** the cut-off text is not in the conversation history

#### Scenario: Complete text reply is posted

- **GIVEN** the model streams text and ends with finish reason `stop`
- **WHEN** the session handles the reply
- **THEN** the session posts the text as the final reply

#### Scenario: Length reply with a tool call runs the tool

- **GIVEN** the model reply has finish reason `length` and a complete tool call
- **WHEN** the session handles the reply
- **THEN** the session runs the tool
- **AND** the turn does not fail as truncated

#### Scenario: Length reply with only thinking is retried

- **GIVEN** the model reply has finish reason `length`, thinking, and no text
  and no tool call
- **WHEN** the session handles the reply
- **THEN** the session follows the empty-response retry path
- **AND** the turn does not fail as truncated on the first reply

### Requirement: Thinking-only model call is capped

The system SHALL stop one LLM call that streams only thinking at three times the
no-progress timeout. The no-progress timeout is `NoProgressTimeout` for a
session and `SubAgents.NoProgressTimeoutSeconds` for a subagent. The
`ProcessingWatchdog` owns the cap timer, and the timer is actor-local. A
thinking update SHALL reset the no-progress deadline but SHALL NOT restart the
cap. Only an answer update SHALL restart the cap. An answer update is non-empty
text, a tool call, or a finish reason. Empty or whitespace-only text counts as
thinking. A reasoning part counts as thinking, also when its text is empty. When
the cap fires in a session, the turn SHALL end with an error that names
thinking. The error category SHALL be `ErrorCategory.ModelBehavior`, because the
provider streamed without fault and the model gave no answer.

#### Scenario: Thinking-only stream stops at the cap with a model error

- **GIVEN** an LLM streaming call is in progress
- **AND** the call streams thinking deltas at short intervals and no answer
  update
- **WHEN** three times the no-progress timeout elapse since the call start
- **THEN** the watchdog fires and the turn ends with an error
- **AND** the error category is `ErrorCategory.ModelBehavior`
- **AND** the error message says that the model streamed only thinking

#### Scenario: Thinking before an answer is not stopped

- **GIVEN** an LLM streaming call has streamed thinking for longer than the
  no-progress timeout but shorter than the cap
- **WHEN** a non-empty text delta arrives
- **THEN** the call continues
- **AND** the cap restarts from that answer update

#### Scenario: Empty text does not restart the cap

- **GIVEN** an LLM streaming call streams a thinking delta and an empty text
  delta in turn, at short intervals
- **WHEN** three times the no-progress timeout elapse since the call start
- **THEN** the turn ends with an error of category `ErrorCategory.ModelBehavior`
- **AND** the error message says that the model streamed only thinking
