## ADDED Requirements

### Requirement: An approval notice is a runtime input of kind child-consent

The session SHALL admit an approval notice as an ordinary input with source kind `child-consent`, the recorded
authority of the run owner with a new turn ID, and the rendered notice text. The kind SHALL make the input automated
input. The input SHALL grant no authority. It SHALL join a running turn only when the turn has the same requester.

#### Scenario: Approval notice joins a turn of the same requester

- **GIVEN** Alice's turn runs and Alice's child asks for consent
- **WHEN** the tool batch of Alice's turn ends
- **THEN** the approval notice joins Alice's turn

#### Scenario: Approval notice does not join another requester's turn

- **GIVEN** Bob's turn runs and Alice's child asks for consent
- **WHEN** the tool batch of Bob's turn ends
- **THEN** the approval notice waits for its own turn

### Requirement: Automated input waits while the session is paused

While the session is paused by an open child consent request, an automated input SHALL wait in the buffer, and it
SHALL NOT join a running turn. An approval notice SHALL NOT wait. A current-session reminder fire SHALL be admitted and
acknowledged, and then it SHALL wait. When a later fire of the same reminder arrives while an earlier fire waits for
the pause, the session SHALL close the earlier fire as superseded and keep the later fire. When the pause ends, at
most one turn of each reminder SHALL run, and the closing notice SHALL list the held fires. The session SHALL post no
thread message for a held fire. A fire that waits behind a parent consent request SHALL keep the current rule.

#### Scenario: The later fire replaces the earlier fire

- **GIVEN** the session is paused
- **WHEN** reminder R fires at 14:00 and again at 15:00
- **THEN** the 14:00 fire is closed as superseded
- **AND** after the pause ends, one turn of R runs

#### Scenario: An isolated reminder session never pauses

- **GIVEN** a reminder runs in its own session with no interactive channel
- **WHEN** its child needs consent
- **THEN** the child is denied without a prompt
- **AND** the session does not pause
