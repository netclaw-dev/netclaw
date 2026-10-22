## ADDED Requirements

### Requirement: A restart-resume message is never an ordinary input

The session SHALL treat the restart reminder as a signal to resume stored work. The session SHALL NOT admit it as an input, and its text SHALL NOT reach the model as a user message.

- In `Ready` phase with pending input and no parked approval, the session SHALL resume that input.
- In `Processing` or `Compacting` phase, the session SHALL queue each pending input that no turn selected and that the queue does not hold yet. It SHALL log `restart_resume_queued` and reply `RestartResumeNotNeeded`, also when it queued nothing. No turn of this message runs: each queued input runs later under its own recorded source. The normal drain SHALL run those inputs after the running turn. They run in admission order among themselves, after the inputs that the queue holds already. The child consent pause and the parent approval waits still apply.
- In `Ready` phase with nothing to resume, or in `Ready` phase when a parked approval owns the next turn, the session SHALL reply `RestartResumeNotNeeded` (a `CommandAck`), make no change, and log `restart_resume_noop`. This includes a redelivery after the resumed turn ended.
- `RestartResumeNotNeeded` SHALL tell the caller that this message resumes nothing and that no turn will run for it. A caller on the Reminder-origin route (#2491) SHALL then settle its run as success at once and SHALL NOT wait for a terminal turn.
- A reply that resumes a turn in `Ready` phase SHALL be an ordinary `CommandAck`, and the caller SHALL wait for the resumed turn. A reply that only queues work SHALL be `RestartResumeNotNeeded`, and the caller SHALL settle at once.
- A refusal (`CommandNack`) SHALL remain only for two causes: a pending input whose stored authority is invalid, and a restart reminder that arrives in `Processing`, `Compacting`, or `Passivating` phase while a restart drain is pending.
- In `Ready` phase, the session SHALL check for a parked approval before it checks the stored authority. In `Processing` or `Compacting` phase, the session does not check for a parked approval. It checks for a pending restart drain, and then it checks the stored authority.

#### Scenario: A redelivered restart message during the resumed turn

- **GIVEN** a restart reminder resumed the pending input and the turn runs
- **WHEN** the same restart reminder arrives again
- **THEN** the session replies `RestartResumeNotNeeded`
- **AND** it stores no input

#### Scenario: A restart message with nothing pending

- **GIVEN** the resumed turn ended and the daemon restarted
- **WHEN** a redelivery of the restart reminder arrives
- **THEN** the session replies `RestartResumeNotNeeded`, not `CommandNack`
- **AND** it stores no input and makes no model call
- **AND** the resume text never reaches the model

#### Scenario: A restart message during another turn queues the pending input

- **GIVEN** recovery started the turn of an orphan child result notice
- **AND** Alice's input P stays pending from before the restart
- **WHEN** the restart reminder arrives while the notice turn runs
- **THEN** the session queues P and replies `RestartResumeNotNeeded`
- **AND** a caller on the Reminder-origin route settles its run as success at once
- **AND** P runs in its own turn after the notice turn ends

#### Scenario: A restart message during another turn with nothing to queue

- **GIVEN** a turn runs, and every pending input is selected by that turn or is in the queue already
- **WHEN** the restart reminder arrives
- **THEN** the session replies `RestartResumeNotNeeded`
- **AND** it queues no input a second time

#### Scenario: A restart message with a damaged pending input during another turn

- **GIVEN** a turn runs, and a pending input from before the restart has a stored authority that does not parse
- **WHEN** the restart reminder arrives
- **THEN** the session replies `CommandNack`
- **AND** it queues no input

#### Scenario: A no-op acknowledgement on the Reminder-origin route ends the run

- **GIVEN** a Reminder-origin turn resumed after a restart and its turn ended
- **WHEN** the restart reminder arrives again and the session replies `RestartResumeNotNeeded`
- **THEN** the reminder run settles as success at once
- **AND** it does not wait for the 20-minute stall timeout

#### Scenario: An acknowledgement that resumes a turn does not end the run

- **GIVEN** a Reminder-origin restart message with a pending input
- **WHEN** the session resumes the input and replies `CommandAck`
- **THEN** the run stays open until the resumed turn ends
