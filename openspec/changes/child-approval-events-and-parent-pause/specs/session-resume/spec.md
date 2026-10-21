## ADDED Requirements

### Requirement: Recovery keeps parked child consent requests

At recovery, the session SHALL decide each child run from the parent run record:

- a parked run with an open consent request SHALL keep its requests open and SHALL have no actor;
- a parked run whose consent requests all have answers SHALL resume (nothing ran before the stop);
- an accepted or cancelling run SHALL be recorded as lost, and its open consent requests SHALL be withdrawn with the
  reason "daemon restarted";
- a reported run with no purge record SHALL have its child journal purged.

The pause SHALL come back while a parked request is open. A pending approval notice whose requests stay open SHALL
resume like an orphan child notice. Recovery SHALL close a pending approval notice only when it withdrew all of its
requests. A new output subscriber SHALL receive each open child prompt again. A session whose only child runs are
parked SHALL be able to passivate.

#### Scenario: Restart with a parked child

- **GIVEN** a child parked at an open consent request
- **WHEN** the daemon restarts
- **THEN** the consent request stays open
- **AND** the session is paused
- **AND** no child actor runs

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
