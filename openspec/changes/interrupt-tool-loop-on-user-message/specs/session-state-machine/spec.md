## ADDED Requirements

### Requirement: Processing distinguishes tool-loop interruption from buffering

While in `Processing`, the session actor SHALL distinguish an active tool-loop
continuation from other processing work. A real user message received during an
active tool-loop continuation SHALL trigger interruption cleanup and fresh-turn
processing rather than the normal mid-processing buffer drain path.

Restart-drain processing remains non-interruptible. Compaction buffering remains
unchanged.

#### Scenario: Processing user message interrupts active tool batch

- **GIVEN** the session actor is in `Processing`
- **AND** an active tool batch or tool-loop continuation is associated with the
  current turn
- **WHEN** a real `SendUserMessage` is received
- **THEN** the actor acknowledges the message
- **AND** initiates interruption cleanup for the current tool loop
- **AND** starts the received message through fresh-turn processing after cleanup

#### Scenario: Stale callbacks cannot continue abandoned turn

- **GIVEN** a user message has interrupted an active tool-loop continuation
- **AND** the actor has cleared or abandoned the old active tool batch
- **WHEN** a late LLM response or tool completion callback from the abandoned
  work arrives
- **THEN** the actor SHALL ignore the stale callback for turn-continuation
  purposes
- **AND** SHALL NOT call the LLM again for the abandoned turn

#### Scenario: Restart drain remains non-interruptible

- **GIVEN** a coordinated restart drain is pending while the session is in
  `Processing`
- **WHEN** a real `SendUserMessage` is received
- **THEN** the actor SHALL preserve the existing restart-drain behavior
- **AND** SHALL NOT start a fresh turn for that message
