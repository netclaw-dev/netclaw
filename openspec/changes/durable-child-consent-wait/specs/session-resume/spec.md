## ADDED Requirements

### Requirement: Recovery keeps parked child runs

The run record of a parked child SHALL hold its consent requests and their answers, so a snapshot keeps them. Parked
child requests SHALL NOT block a routine snapshot. At recovery, the session SHALL decide each child run from its run
record:

- a parked run with an open consent request SHALL keep its requests open and SHALL have no actor;
- a parked run whose requests all have answers and that has no resume record SHALL resume (nothing ran);
- an accepted or cancelling run SHALL be recorded as lost, and its open consent requests SHALL be withdrawn with the
  reason "daemon restarted".

The child consent pause SHALL come back while a parked request is open. The input that the pause held SHALL wait until
the last open request closes. An answer that arrives during a restart drain SHALL be recorded, and recovery SHALL resume
the run. A session whose only child runs are parked SHALL be able to passivate.

#### Scenario: Restart with a parked child

- **GIVEN** a child parked at an open consent request
- **WHEN** the daemon restarts
- **THEN** the consent request stays open and the session is paused
- **AND** no child actor runs
- **AND** a click on the old prompt resumes the parked call once

#### Scenario: A compaction snapshot keeps the park and its answers

- **GIVEN** a child parked at two consent requests p1 and p2
- **WHEN** the requester approves p1
- **AND** the session compacts, saves a snapshot, and deletes the journal rows before it
- **AND** the daemon restarts
- **AND** the requester approves p2
- **THEN** both parked calls run, each once

#### Scenario: Crash after the last answer

- **GIVEN** a parked child whose last request has an answer and no resume record
- **WHEN** the session recovers
- **THEN** the session resumes the child and the parked call runs once

#### Scenario: A held reminder fire waits for the last answer after a restart

- **GIVEN** a parked child with an open request and a held reminder fire
- **WHEN** the daemon restarts
- **THEN** the fire does not run
- **WHEN** the requester answers the request
- **THEN** the fire runs once after the closing notice

#### Scenario: Session with only parked children passivates

- **GIVEN** a session whose only child run is parked
- **WHEN** the idle timeout passes
- **THEN** the session passivates
- **AND** a later click on the prompt activates it and resumes the child
