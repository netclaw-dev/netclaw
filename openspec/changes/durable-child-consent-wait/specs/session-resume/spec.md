## ADDED Requirements

### Requirement: Recovery keeps parked child runs

The run record of a parked child SHALL hold its park, its consent requests, and their answers, so a snapshot keeps
them. Parked child requests SHALL NOT block a routine snapshot by themselves. At recovery, the session SHALL decide each
child run from its run record:

- a parked run with an open consent request SHALL keep its requests open and SHALL have no actor;
- a parked run whose requests all have answers and that has no resume record SHALL resume (nothing ran);
- an accepted or cancelling run SHALL be recorded as lost, and its open consent requests SHALL be withdrawn with the
  reason "daemon restarted".

The child consent pause SHALL come back while a parked request is open. The input that the pause held SHALL wait until
the last open request closes, and a reminder SHALL run at most once for its held fires, also when a new fire of the
same reminder arrives during recovery. A parked run with an open request SHALL NOT make the session eligible for a
restart reminder. A parked run whose requests all have answers SHALL make it eligible. A session whose only child runs
are parked with open requests SHALL be able to passivate.

An answer that arrives while a restart drain is pending and the session still runs or waits SHALL be recorded, and
recovery SHALL resume the run. An answer that arrives while the session passivates for the drain SHALL get the restart
message, and its prompt SHALL stay open.

#### Scenario: Restart with a parked child

- **GIVEN** a child parked at an open consent request
- **WHEN** the daemon restarts
- **THEN** the consent request stays open and the session is paused
- **AND** no child actor runs
- **AND** a click on the old prompt resumes the parked call once

#### Scenario: A wrong user after recovery

- **GIVEN** Alice's child parked at an open consent request, and a daemon restart
- **WHEN** Bob clicks the old prompt
- **THEN** the session rejects the answer as the wrong requester
- **AND** nothing is recorded and the call does not run

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

#### Scenario: Crash after the resume record

- **GIVEN** a child with a resume record and no later checkpoint or end
- **WHEN** the session recovers
- **THEN** the run is recorded as lost
- **AND** the parked call does not run again

#### Scenario: A held reminder runs once after a restart

- **GIVEN** a parked child with an open request and a held fire F of reminder M
- **WHEN** the daemon restarts and a new fire F2 of M arrives during recovery
- **THEN** F2 replaces F and neither runs while the request is open
- **WHEN** the requester answers the request
- **THEN** M runs once after the closing notice

#### Scenario: Session with only parked children passivates

- **GIVEN** a session whose only child run is parked with an open request
- **WHEN** the idle timeout passes
- **THEN** the session passivates
- **AND** a later click on the prompt activates it and resumes the child
