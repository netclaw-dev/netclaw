## ADDED Requirements

### Requirement: Recovery keeps parked child consent requests

Until a parked run resumes, the parent snapshot SHALL hold every consent request of the run, open or answered:
requester, options, candidates, working directory, child context, authorization attempt, and the answer when one
exists. The session SHALL clear these entries when the run resumes. Parked child requests SHALL NOT block a routine
snapshot or a compaction snapshot. At recovery, the session SHALL decide each child run from the parent run record:

- a parked run with an open consent request SHALL keep its requests open and SHALL have no actor;
- a parked run whose consent requests all have answers and that has no resume record SHALL resume (nothing ran);
- an accepted or cancelling run SHALL be recorded as lost, and its open consent requests SHALL be withdrawn with the
  reason "daemon restarted";
- an ended run that has a child journal and no purge record SHALL have its child journal purged.

Recovery SHALL close every pending approval notice and SHALL rebuild the notice digest from the open requests. The
pause SHALL come back while a parked request is open. A new output subscriber SHALL receive each open child prompt
again. A session whose only child runs are parked SHALL be able to passivate.

#### Scenario: Restart with a parked child

- **GIVEN** a child parked at an open consent request
- **WHEN** the daemon restarts
- **THEN** the consent request stays open
- **AND** the session is paused
- **AND** no child actor runs

#### Scenario: Compaction snapshot keeps the parked request

- **GIVEN** a child parked at an open consent request
- **WHEN** the session compacts, saves a snapshot, and deletes the journal rows before it
- **AND** the daemon restarts
- **THEN** a click on the old prompt resumes the parked call once

#### Scenario: Compaction keeps an answer of a parked run

- **GIVEN** a child parked at two consent requests p1 and p2
- **WHEN** the operator approves p1
- **AND** the session compacts, saves a snapshot, and deletes the journal rows before it
- **AND** the daemon restarts
- **AND** the operator approves p2
- **THEN** the resume runs both parked calls, each once

#### Scenario: Crash after the last answer and before the resume record

- **GIVEN** a parked child whose last consent request has an answer and no resume record
- **WHEN** the session recovers
- **THEN** the session resumes the child and the parked call runs once

#### Scenario: Crash after the resume record

- **GIVEN** a child with a resume record and no later checkpoint or end
- **WHEN** the session recovers
- **THEN** the run is recorded as lost
- **AND** the parked call is not run again

#### Scenario: Session with only parked children passivates

- **GIVEN** a session whose only child run is parked
- **WHEN** the idle timeout passes
- **THEN** the session passivates
- **AND** a later click on the prompt activates it and resumes the child

## MODIFIED Requirements

### Requirement: A restart-resume message is never an ordinary input

The session SHALL treat the restart reminder as a signal to resume stored work. The session SHALL NOT admit it as an input, and its text SHALL NOT reach the model as a user message.

- In `Ready` phase with pending input, the session SHALL resume that input.
- In `Processing` or `Compacting` phase, the session SHALL queue each pending input that no turn selected and that the queue does not hold yet. It SHALL log `restart_resume_queued` and reply `RestartResumeNotNeeded`, also when it queued nothing. No turn of this message runs: each queued input runs later under its own recorded source. The normal drain SHALL run those inputs after the running turn, in admission order.
- In `Ready` phase with nothing to resume, or when a parked approval owns the next turn, the session SHALL reply `RestartResumeNotNeeded` (a `CommandAck`), make no change, and log `restart_resume_noop`. This includes a redelivery after the resumed turn ended.
- `RestartResumeNotNeeded` SHALL tell the caller that this message resumes nothing and that no turn will run for it. A caller on the Reminder-origin route (#2491) SHALL then settle its run as success at once and SHALL NOT wait for a terminal turn.
- A reply that resumes a turn in `Ready` phase SHALL be an ordinary `CommandAck`, and the caller SHALL wait for the resumed turn. A reply that only queues work SHALL be `RestartResumeNotNeeded`, and the caller SHALL settle at once.
- A refusal (`CommandNack`) SHALL remain only for a restart reminder whose stored authority is invalid. This SHALL hold in every phase, also when the session would queue the input.

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
