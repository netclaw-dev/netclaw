## ADDED Requirements

### Requirement: A restart-resume message is never an ordinary input

The session SHALL treat the restart reminder as a signal to resume stored work. The session SHALL NOT admit it as an input, and its text SHALL NOT reach the model as a user message.

- In `Ready` phase with pending input, the session SHALL resume that input.
- In every other case, the session SHALL reply `CommandAck`, make no change, and log `restart_resume_noop`. This includes a redelivery during the resumed turn, a redelivery after the turn ended, and a message that arrives while another turn runs.
- A no-op reply SHALL tell the caller that nothing was resumed (`CommandAck.RestartResumeNoOp`). A caller on the Reminder-origin route (#2491) SHALL then settle its run as success at once and SHALL NOT wait for a terminal turn, because no turn will run.
- A reply that resumes work SHALL be an ordinary `CommandAck`, and the caller SHALL wait for the resumed turn.
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

#### Scenario: A no-op acknowledgement on the Reminder-origin route ends the run

- **GIVEN** a Reminder-origin turn resumed after a restart and its turn ended
- **WHEN** the restart reminder arrives again and the session replies with a no-op acknowledgement
- **THEN** the reminder run settles as success at once
- **AND** it does not wait for the 20-minute stall timeout

#### Scenario: An acknowledgement that resumes a turn does not end the run

- **GIVEN** a Reminder-origin restart message with a pending input
- **WHEN** the session resumes the input and replies `CommandAck`
- **THEN** the run stays open until the resumed turn ends
