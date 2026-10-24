## ADDED Requirements

### Requirement: The drain acknowledgement waits for running children

The session SHALL bound its wait for children with a child wait. The child wait SHALL be the configured drain deadline
minus a fixed acknowledgement margin, so that the session acknowledges before the caller stops waiting. Both drain
callers SHALL use the same configured drain deadline. While the session has a live
child run and the child wait has not ended, the session SHALL NOT acknowledge the drain. The session SHALL continue
the drain only after the journal holds the park record or the terminal record of the last live child. When the child
wait ends with a live child, the session SHALL log one Warning with the count of such children and SHALL acknowledge
the drain. The restart reminder SHALL then count a parked run with no open request and a running run as unreported, as
it does today. The wait applies only to child runs. A tool of the parent turn that still runs keeps the existing rule:
the session does not acknowledge the drain.

#### Scenario: The acknowledgement comes after the park record

- **GIVEN** a session in the `Ready` phase with one child run whose tool batch runs
- **WHEN** a graceful drain starts, and the batch ends inside the child wait
- **THEN** the session acknowledges the drain after the journal holds the park record of the run
- **AND** the acknowledgement carries a restart reminder from the recorded authority of the run

#### Scenario: The child wait ends before the deadline of the caller

- **GIVEN** a session with one child run whose tool does not end
- **WHEN** a graceful drain starts, and the child wait ends before the drain deadline
- **THEN** the session logs one Warning that names one live child when the child wait ends
- **AND** the session acknowledges the drain with a restart reminder before the drain deadline
- **AND** the shutdown alert does not name the session as timed out

#### Scenario: A session with no live child does not wait

- **GIVEN** a session in the `Ready` phase with no live child run
- **WHEN** a graceful drain starts
- **THEN** the session acknowledges the drain without the child wait
