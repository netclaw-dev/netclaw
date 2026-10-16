## MODIFIED Requirements

### Requirement: LLM invocation encapsulation

LLM call execution and streaming SHALL be encapsulated in a `SessionLlmInvoker`
static utility class. The invoker SHALL handle timeout wrapping, streaming delta
forwarding via `self.Tell(LlmResponseDeltaReceived)`, and error packaging as
`LlmCallFailed`. Dynamic context layer injection SHALL be a static method on
this class.

#### Scenario: LLM streaming inactivity timeout

The system SHALL enforce a reset-on-delta inactivity timeout for LLM
streaming calls using `FirstTokenTimeout` (default 600s). The timer starts
when the LLM call is fired and resets on every streaming delta received,
including a thinking delta.

- **GIVEN** an LLM streaming call is in progress
- **WHEN** no deltas arrive within `FirstTokenTimeout` of the call start or
  the last received delta
- **THEN** the watchdog fires and the turn fails with `ErrorCategory.Timeout`
- **AND** the error message indicates the stream timed out due to inactivity

Backward compat: if `TurnLlmTimeoutSeconds` is configured but
`FirstTokenTimeoutSeconds` is not, `FirstTokenTimeout` uses `TurnLlmTimeout`.

#### Scenario: Thinking-only stream stops at the thinking cap

The system SHALL stop one LLM call that streams only thinking at three times
the no-progress timeout. A thinking delta resets the inactivity deadline but
does not restart the cap. Only an answer update restarts the cap. An answer
update is non-empty text, a tool call, or a finish reason. Empty or
whitespace-only text counts as thinking.

- **GIVEN** an LLM streaming call is in progress
- **AND** the call streams thinking deltas at short intervals and no answer
  update
- **WHEN** three times the no-progress timeout elapse since the call start
- **THEN** the watchdog fires and the turn fails with `ErrorCategory.Timeout`
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
- **THEN** the turn fails with `ErrorCategory.Timeout`
- **AND** the error message says that the model streamed only thinking

#### Scenario: Streaming deltas forwarded to actor

- **GIVEN** an LLM streaming call is in progress
- **WHEN** text content chunks arrive
- **THEN** each chunk after the first is forwarded as `LlmResponseDeltaReceived`
- **AND** the first chunk is held until the second arrives (single-chunk optimization)
- **AND** the watchdog refreshes with `FirstTokenTimeout` on each delta

## ADDED Requirements

### Requirement: Truncated model reply fails the turn

The system SHALL NOT complete a turn as success when the model reply has finish
reason `length` and no tool call. The turn SHALL end with an error that says the
reply was truncated. The session SHALL NOT post the cut-off text as the final
reply and SHALL NOT add it to the conversation history. A reply with finish
reason `length` and a tool call SHALL run its tools. A thinking-only or empty
reply with finish reason `length` SHALL follow the existing empty-response retry
path.

#### Scenario: Cut-off text reply ends the turn with an error

- **GIVEN** the model streams text and ends with finish reason `length`
- **AND** the reply has no tool call
- **WHEN** the session handles the reply
- **THEN** the turn ends with an error that says the reply was truncated
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
