## ADDED Requirements

### Requirement: A running child parks at a graceful drain

At a graceful drain, the session SHALL tell each live child run to park. After this signal, the child SHALL start no
model call and no tool batch. A child whose tool batch runs SHALL park when the batch ends. A child that waits for a
model response SHALL cancel that call and park at once, with the state that it had before the call. The park of a
running child (a drain park) SHALL hold no consent request. When the batch ends with a call that needs consent, the
child SHALL make a consent park and no drain park.

The session SHALL record a drain park in the run record with the same durable record as a consent park, before it
acknowledges the drain. A drain park SHALL NOT need a requester who can answer a prompt, because it asks nobody. A
consent park SHALL still need one. A drain park SHALL NOT pause the session, because it opens no consent request.

A child that does not park before the child wait of the drain ends SHALL stay a running run in the record. Recovery
SHALL record it as lost with the reason `owner_restarted`, as for any running run. A tool call that ran at that time
SHALL NOT run again. A drain park SHALL NOT add a round limit or a tool-call limit to the child.

#### Scenario: A child parks when its tool batch ends

- **GIVEN** Alice's child run runs a tool batch with one call
- **WHEN** a graceful drain starts, and the batch then ends
- **THEN** the child starts no model call
- **AND** the run record holds a park with zero consent requests and the result of the call
- **AND** the session acknowledges the drain with a restart reminder

#### Scenario: A child parks during a model call

- **GIVEN** Alice's child run waits for its first model response
- **WHEN** a graceful drain starts
- **THEN** the child cancels the model call and parks at once
- **AND** the park holds the conversation that the child sent to the model, and no response of the cancelled call

#### Scenario: A consent need makes a consent park during the drain

- **GIVEN** Alice's child run runs a tool batch with a sibling call and a call that needs consent
- **WHEN** a graceful drain starts, and the batch then ends
- **THEN** the run record holds one park with one consent request for Alice
- **AND** the session holds no second park for the run

#### Scenario: A child of an automation turn parks with no prompt

- **GIVEN** a reminder turn with no interactive approval started a child run, and the child runs a tool batch
- **WHEN** a graceful drain starts, and the batch then ends
- **THEN** the run record holds a drain park, and the run does not end
- **AND** no prompt shows

#### Scenario: A consent park still needs a requester who can answer

- **GIVEN** a child run of a requester with no interactive approval reaches a park with one consent request
- **WHEN** the session receives the park
- **THEN** the run ends as failed and no prompt shows
- **AND** the parked call does not run

#### Scenario: A child that misses the wait is lost

- **GIVEN** Alice's child run runs a tool that does not end before the child wait of the drain ends
- **WHEN** the daemon restarts and the session recovers
- **THEN** recovery records the run as lost with the reason `owner_restarted`
- **AND** the tool does not run again

### Requirement: A drain park resumes once after the restart

At recovery, the session SHALL resume a drain park with the same rules as a consent park: the resume record is in the
journal before the child starts, and a crash after that record makes the run lost. A drain park has no request to
wait for, so the session SHALL resume it at recovery and SHALL NOT resume it during a drain. The resumed child SHALL
keep the conversation, the round, the loop guard, the file activity, and the working directories of the park. It
SHALL start with a model call and SHALL run no tool call of the park again. It SHALL run under the recorded authority
of the run, and the tool gate SHALL check each later call. The status of the resumed run SHALL NOT say that the child
resumed after consent.

#### Scenario: A drain park resumes after the restart

- **GIVEN** Alice's child run made a drain park after its third tool round
- **WHEN** the daemon restarts and the restart reminder wakes the session
- **THEN** the journal holds one resume record for the run before the child starts
- **AND** the child makes its next model call with the conversation of the park and with round 3
- **AND** the run ends once, and Alice's session gets one result notice

#### Scenario: A resumed child keeps the recorded authority

- **GIVEN** a drain park of a child run that Alice started in her thread
- **WHEN** Bob writes in the session after the restart, and the child resumes
- **THEN** each tool call of the resumed child runs with Alice's recorded audience, boundary, and delivery targets
- **AND** Bob's authority does not reach the child

#### Scenario: A crash after the resume record does not repeat work

- **GIVEN** a drain park whose resume record is in the journal
- **WHEN** the daemon crashes before the resumed child ends
- **THEN** recovery records the run as lost with the reason `owner_restarted`
- **AND** recovery does not start the child again

#### Scenario: A drain park does not resume during the drain

- **GIVEN** a child run made a drain park
- **WHEN** the drain continues
- **THEN** no child of the run starts before the restart
