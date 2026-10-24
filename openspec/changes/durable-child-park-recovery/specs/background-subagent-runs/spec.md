## ADDED Requirements

### Requirement: A park and its resume are durable in the run record

The session SHALL record a park, its consent requests, and the prompt counter of the run in one durable write in the
run record, before it shows any prompt of the park. The session SHALL record each answer of a parked request in the
run record when the journal holds the answer, so a snapshot after the answer keeps it. The session SHALL record the
resume in the run record before any parked call runs. A snapshot SHALL hold a parked run with its park, its requests,
their answers, and its prompt counter. A second park of the same run SHALL NOT reuse a prompt number of the run, also
after a restart.

When the journal rejects the resume record, the run SHALL end as failed with the reason `tool_execution_failed` and the
text "The parked calls did not run.", and no child actor SHALL start. A rejected record of a run SHALL end the run only
when the run still has the state that the record expected. A rejected answer that arrives after a duplicate answer
already resumed the run SHALL NOT end the run.

After a resume and a later snapshot, the session no longer holds the prompt of the parked call. A click on that prompt
SHALL get the existing closed-call reply, and nothing SHALL run.

At recovery, the session SHALL log the count and the total size in bytes of the parked runs, with no content, only when
the count is above zero.

#### Scenario: A rejected resume record ends the run with the reused reason

- **GIVEN** a parked child whose last request gets an answer
- **WHEN** the journal rejects the resume record
- **THEN** the run ends as failed with the reason `tool_execution_failed`
- **AND** the notice says "The parked calls did not run."
- **AND** no child actor starts and the parked call does not run

#### Scenario: A late rejection does not end a resumed run

- **GIVEN** a parked child with one request, and the journal rejects the first answer
- **WHEN** a duplicate answer of the requester is recorded first and resumes the run
- **AND** the rejection of the first answer arrives after that
- **THEN** the run continues and ends once, with the result of the resumed child

#### Scenario: The prompt counter survives a restart

- **GIVEN** a child that parked once, resumed, and parks again after a restart
- **WHEN** the session shows the prompts of the second park
- **THEN** no prompt of the second park has the call ID of a prompt of the first park

#### Scenario: A late click after a resume and a snapshot

- **GIVEN** a parked call that resumed and ran, and a later snapshot of the session
- **WHEN** the requester clicks the old prompt again
- **THEN** the session gives the closed-call reply
- **AND** nothing is recorded and the call does not run again

#### Scenario: Recovery logs the parked runs

- **GIVEN** a session with two parked runs
- **WHEN** the session recovers
- **THEN** the log has one line with the count 2 and the total size of the two parks in bytes, and no park content

#### Scenario: Recovery with no park writes no park line

- **GIVEN** a session with no parked run
- **WHEN** the session recovers
- **THEN** the log has no line about parked runs

### Requirement: A damaged park ends at the answer

A park whose loop-guard state cannot be valid SHALL NOT stop recovery. Recovery SHALL keep such a run parked with its
prompts open. When every request of the run has an answer, the session SHALL end the run as failed with the reason
`park_damaged`, and no parked call SHALL run. A loop-guard state cannot be valid when a count is negative, when the
refused-repeat count is above the escalation count plus one, or when an equal-round count is outside 0 to 2.

#### Scenario: A valid park resumes

- **GIVEN** a parked child with a valid loop-guard state
- **WHEN** the requester approves the parked request
- **THEN** the parked call runs once

#### Scenario: A damaged park ends with park_damaged

- **GIVEN** a parked child whose recorded loop-guard state has an equal-round count of 7
- **WHEN** the daemon restarts
- **THEN** recovery completes and the prompt stays open
- **WHEN** the requester approves the parked request
- **THEN** the run ends as failed with the reason `park_damaged`
- **AND** the parked call does not run

### Requirement: Park records are forward-only

A session journal or snapshot that holds a park record, a resume record, or a parked run SHALL be readable only by a
binary with this change. An older binary SHALL fail recovery of that session loudly. It SHALL NOT skip the record and
SHALL NOT run a parked call. A record that an older binary wrote SHALL stay readable: a child consent request of an
older live wait belongs to a run that is not parked, so recovery withdraws it with the reason "daemon restarted".

#### Scenario: An upgrade keeps an older live-wait request safe

- **GIVEN** a session journal from 0.28.0-beta.2 with an open child consent request of a live wait
- **WHEN** a binary with this change recovers the session
- **THEN** the run is recorded as lost and the request is withdrawn with the reason "daemon restarted"
- **AND** the call does not run

#### Scenario: An older binary refuses a park record

- **GIVEN** a session journal that holds a park record
- **WHEN** an older binary recovers the session
- **THEN** recovery of that session fails with an error
- **AND** no parked call runs
