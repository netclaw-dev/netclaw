## ADDED Requirements

### Requirement: A restart-resume message is never an ordinary input

The session SHALL treat the restart reminder as a signal to resume stored work. The session SHALL NOT admit it as an input, and its text SHALL NOT reach the model as a user message.

- In `Ready` phase with pending input, the session SHALL resume that input.
- In every other case, the session SHALL reply `CommandAck`, make no change, and log `restart_resume_noop`. This includes a redelivery during the resumed turn, a redelivery after the turn ended, and a message that arrives while another turn runs.
- A refusal (`CommandNack`) SHALL remain only for a restart reminder whose stored authority is invalid.

#### Scenario: A redelivered restart message during the resumed turn

- **GIVEN** a restart reminder resumed the pending input and the turn runs
- **WHEN** the same restart reminder arrives again
- **THEN** the session replies `CommandAck`
- **AND** it stores no input

#### Scenario: A restart message with nothing pending

- **GIVEN** the resumed turn ended and the daemon restarted
- **WHEN** a redelivery of the restart reminder arrives
- **THEN** the session replies `CommandAck`, not `CommandNack`
- **AND** it stores no input and makes no model call
- **AND** the resume text never reaches the model
