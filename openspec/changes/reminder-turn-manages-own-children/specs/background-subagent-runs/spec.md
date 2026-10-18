## ADDED Requirements

### Requirement: A reminder turn shares child runs with the people of its own session

A reminder turn and a person turn of the same session SHALL check the status of each other's child runs, and SHALL cancel them. The system SHALL ignore the sender ID and the principal classification when one of the two sides is a reminder turn. The side that is a reminder turn is the requester, or the turn that started the run. The system SHALL still require the same session, the same audience, the same trust boundary, and the same channel type as the original context of the run.

A reminder turn is a turn whose recorded principal classification is `VerifiedAutomation` and whose recorded source kind is `reminder`. A turn that has only one of these two facts is not a reminder turn. The rule for two turns that are not reminder turns SHALL NOT change. Such a requester owns a run only when all six requester facts match: session, audience, trust boundary, channel type, sender ID, and principal classification.

A reminder turn SHALL NOT see or control a child run of another session. A person turn SHALL NOT see or control a child run that a reminder turn started in another session. A reminder fire that delivers to another session or to a channel runs in a session that has no relation to the run. It owns no run of the person's session.

In a shared channel session, a person turn of any sender can control a child run that a reminder turn started. A reminder turn can control a child run that any person started in the session. The owner accepted this effect. The session is the unit of trust for a reminder.

The decision belongs to the session actor that owns the run. The original context of the run is durable in the session journal. The context of the request is call-local to the tool batch that asks.

The refusal text SHALL stay the same for a missing run and for a run of another requester.

#### Scenario: A reminder turn checks a child run that a person started

- **GIVEN** an operator turn (sender `local`) in a session started a child run
- **AND** a `current_session` reminder in the same session, with the same audience, boundary, and channel type, fires later
- **WHEN** the reminder turn asks for the status of the run
- **THEN** the system returns the status and the pending approval count
- **AND** it does not return the not-available error

#### Scenario: A reminder turn cancels a child run that a person started

- **GIVEN** an operator turn in a session started a child run that is still accepted
- **WHEN** a reminder turn of the same session asks to cancel the run
- **THEN** the system persists the cancellation intent
- **AND** it acknowledges the intent without reporting terminal cancellation

#### Scenario: A person turn controls a child run that a reminder turn started

- **GIVEN** a reminder turn in a session started a child run
- **WHEN** a person turn of the same session, with the same audience, boundary, and channel type, asks for the status of the run or asks to cancel it
- **THEN** the system returns the status and the pending approval count
- **AND** it persists the cancellation intent for the cancel request

#### Scenario: Another person controls a child run that a reminder turn started

- **GIVEN** a reminder turn in a shared channel session started a child run
- **WHEN** Bob, a person turn in the same session, asks for the status of the run or asks to cancel it
- **THEN** the system returns the status and persists the cancellation intent

#### Scenario: A later reminder turn controls a child run that a reminder turn started

- **GIVEN** a reminder fire in a session started a child run
- **WHEN** a later fire in the same session asks for the status of the run
- **THEN** the system returns the status and the pending approval count

#### Scenario: A reminder turn cannot reach a child run of another session

- **GIVEN** an operator turn in session A started a child run
- **WHEN** a reminder turn in session B asks for the status of that run, or asks to cancel it
- **THEN** the system denies both requests with the not-available error
- **AND** it persists no cancellation intent

#### Scenario: A person turn cannot reach a child run that a reminder turn started in another session

- **GIVEN** a reminder turn in session A started a child run
- **WHEN** a person turn in session B asks for the status of that run, or asks to cancel it
- **THEN** the system denies both requests with the not-available error
- **AND** it persists no cancellation intent

#### Scenario: A turn with another audience, boundary, or channel type is denied

- **GIVEN** a child run belongs to a session with audience Personal
- **WHEN** a reminder turn of the same session has another audience, another trust boundary, or another channel type
- **THEN** the system denies status and cancel with the not-available error
- **WHEN** a person turn of the same session asks for the status of a run that a reminder turn started, and the person turn has another audience, another trust boundary, or another channel type
- **THEN** the system denies status and cancel with the not-available error

#### Scenario: Another person in the same session is still denied

- **GIVEN** Alice started a child run in a session
- **WHEN** Bob, a person turn in the same session, asks for the status of the run or asks to cancel it
- **THEN** the system denies both requests
- **AND** Bob does not see the pending approval count

#### Scenario: Automation that is not a reminder gets no relaxation

- **GIVEN** an operator turn in a session started a child run
- **WHEN** a turn with principal `VerifiedAutomation` and a source kind other than `reminder` asks for the status of the run
- **THEN** the system denies the request with the not-available error
- **AND** a person turn of another sender is also denied for a run that such a turn started
