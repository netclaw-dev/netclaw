## ADDED Requirements

### Requirement: Recovery keeps parked child consent requests

The parent snapshot SHALL hold the open consent request of each parked child call: requester, options, candidates,
working directory, child context, and authorization attempt. Parked child requests SHALL NOT block a routine snapshot
or a compaction snapshot. At recovery, the session SHALL decide each child run from the parent run record:

- a parked run with an open consent request SHALL keep its requests open and SHALL have no actor;
- a parked run whose consent requests all have answers and that has no resume record SHALL resume (nothing ran);
- an accepted or cancelling run SHALL be recorded as lost, and its open consent requests SHALL be withdrawn with the
  reason "daemon restarted";
- an ended run that has a child journal and no purge record SHALL have its child journal purged.

Recovery SHALL close every pending approval notice and SHALL rebuild the notice digests from the open requests. The
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
