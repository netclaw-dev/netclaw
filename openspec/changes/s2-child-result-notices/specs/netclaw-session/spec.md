## ADDED Requirements

### Requirement: A restart resume runs the leading inputs of one requester

The restart resume SHALL take the leading pending inputs that share the requester of the first pending input. It SHALL run them in one turn. The other pending inputs, including notices, SHALL wait in the queue and run in their own turns in admission order.

#### Scenario: Mixed pending inputs resume in order

- **GIVEN** a pending input of Alice and a notice of Bob
- **WHEN** the restart resume runs
- **THEN** the first request holds Alice's input and not Bob's result
- **AND** Bob's notice runs after Alice's turn

### Requirement: The session produces a notice without an acknowledgement

The session SHALL admit a notice through the ordinary admission path, and SHALL send no acknowledgement for it. The admission SHALL fail before the journal write when the run has a notice already or when the authority differs from the run owner.

#### Scenario: A second notice is refused

- **GIVEN** a run with a notice
- **WHEN** the session tries to admit another notice for it
- **THEN** the journal gets no second notice
