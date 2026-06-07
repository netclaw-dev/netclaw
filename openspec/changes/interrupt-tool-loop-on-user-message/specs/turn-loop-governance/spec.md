## ADDED Requirements

### Requirement: User input interrupts active tool continuations

When a real user message arrives while a session is continuing an active tool
loop, the system SHALL treat that message as an interruption boundary rather
than appending it into the current tool-loop continuation.

The interrupted turn SHALL NOT perform another tool-enabled LLM continuation
after the interrupting message has been accepted. The interrupting message SHALL
start fresh-turn processing after the interrupted tool batch is closed or made
inert.

#### Scenario: User correction stops current tool loop

- **GIVEN** a session turn is waiting for tool results after an assistant
  response requested tools
- **WHEN** a real user message arrives in the same session before the tool loop
  has produced a final assistant response
- **THEN** the session treats the new message as an interruption of the current
  tool loop
- **AND** the session does not append that message into the current tool-loop
  continuation
- **AND** the next LLM call processes the message as a fresh turn

#### Scenario: Interrupted turn does not continue with tools

- **GIVEN** a user message has interrupted an active tool loop
- **WHEN** the actor finishes closing or abandoning the interrupted tool batch
- **THEN** the actor SHALL NOT call the LLM again for the old turn with tools
  enabled
- **AND** any subsequent tool-enabled LLM call belongs to the fresh user turn
