## MODIFIED Requirements

### Requirement: Config hot-reload integration

The session system SHALL participate in config-triggered daemon restart
coordination instead of attempting in-place config mutation. When restart drain
begins, active sessions SHALL stop accepting new turns, stop reopening
passivation because of delivery feedback, complete their current turn or
compaction step when possible within the daemon drain budget, snapshot durable
state before stopping, and acknowledge drain completion to the daemon restart
coordinator.

#### Scenario: Ready session drains immediately

- **GIVEN** a session actor is in its ready state when restart drain begins
- **WHEN** the daemon sends the restart-drain command
- **THEN** the actor enters restart passivation without accepting another turn
- **AND** snapshots its recoverable state before stopping

#### Scenario: Processing session finishes current work before stopping

- **GIVEN** a session actor is processing a user turn when restart drain begins
- **WHEN** the daemon sends the restart-drain command
- **THEN** the actor does not start a new turn
- **AND** completes or fails the in-flight turn before passivating
- **AND** acknowledges drain completion only after durable state is safe to recover

#### Scenario: Incoming user message during restart drain is rejected

- **GIVEN** a session actor has entered restart drain mode
- **WHEN** a new `SendUserMessage` reaches that session
- **THEN** the actor rejects the message with a restart-in-progress reason
- **AND** the message is not buffered for later execution

#### Scenario: Delivery feedback during restart drain does not abort passivation

- **GIVEN** a session actor has entered restart drain mode
- **WHEN** delivery feedback or retry-inducing channel feedback arrives
- **THEN** the actor does NOT leave restart drain mode
- **AND** no new retry turn is started because of that feedback

#### Scenario: Restart drain timeout stops from last durable checkpoint

- **GIVEN** a session actor is still processing when the daemon drain timeout expires
- **WHEN** the daemon forces restart completion
- **THEN** the session stops without claiming uncommitted work succeeded
- **AND** the next recovery starts from the last persisted journal or snapshot state
