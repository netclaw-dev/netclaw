## ADDED Requirements

### Requirement: An approval notice is a runtime input of kind child-consent

The session SHALL admit an approval notice as an ordinary input with source kind `child-consent`, sender
`child-consent`, principal `VerifiedAutomation`, the session, audience, boundary, and channel type of the run owner,
a new turn ID, and the rendered notice text. The input SHALL be automated input and SHALL grant no authority. It SHALL
NOT join a turn of a person. The stored input format SHALL NOT change.

#### Scenario: Approval notice waits for its own turn

- **GIVEN** Alice's turn runs and Alice's child asks for consent
- **WHEN** the tool batch of Alice's turn ends
- **THEN** the approval notice does not join Alice's turn
- **AND** it runs in its own turn after Alice's turn ends

#### Scenario: Notice turn cannot cancel a person's run

- **GIVEN** an approval notice turn runs
- **WHEN** the model calls `check_agent_run` to cancel Alice's child
- **THEN** the call is denied as for another requester
- **AND** the notice text tells the model to ask the operator to cancel

### Requirement: Automated input waits while the session is paused

While the session is paused by an open child consent request, an automated input SHALL wait in the buffer, and it
SHALL NOT join a running turn. An approval notice SHALL NOT wait. A current-session reminder fire SHALL be admitted and
acknowledged, and then it SHALL wait. When a later fire of the same reminder arrives while an earlier fire waits for
the pause, the session SHALL close the earlier fire with the existing input-closed event and keep the later fire. This
rule SHALL apply while the child pause applies, also when a parent consent request is open at the same time. A fire
that waits only behind a parent consent request SHALL keep the current rule. When the pause ends, at most one turn of
each reminder SHALL run, and the notice that reports the end of the pause SHALL list the held fires. The session SHALL
post no thread message for a held fire.

#### Scenario: The later fire replaces the earlier fire

- **GIVEN** the session is paused
- **WHEN** reminder R fires at 14:00 and again at 15:00
- **THEN** the 14:00 fire is closed
- **AND** after the pause ends, one turn of R runs

#### Scenario: Child pause and parent prompt both open

- **GIVEN** a child consent request and a parent consent request are both open
- **WHEN** reminder R fires twice
- **THEN** the later fire replaces the earlier fire

#### Scenario: An isolated reminder session never pauses

- **GIVEN** a reminder runs in its own session with no interactive channel
- **WHEN** its child needs consent
- **THEN** the child is denied without a prompt
- **AND** the session does not pause
