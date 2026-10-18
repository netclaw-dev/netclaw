## MODIFIED Requirements

### Requirement: Child execution keeps the parent's authority

The system SHALL preserve the original requester, audience, and authority boundary for child tools, approvals, status, and cancellation. A child result SHALL NOT grant authority. A child worker SHALL remain ephemeral and SHALL NOT restart an uncertain external action after owner recovery. Status SHALL distinguish durable acceptance from terminal completion. The run owner's status response SHALL include the pending approval count. Other requesters SHALL NOT see that count. One exception applies: a reminder turn and the other turns of its session, as the requirement "A reminder turn shares child runs with the other turns of its own session" states. An approval for a stale or unrelated run, call, requester, or attempt SHALL grant no authority.

The system SHALL persist cancellation intent before it acknowledges the intent. That acknowledgement SHALL NOT mean that the run is complete. The system SHALL report terminal cancellation only after the local dispatch confirms closure, and it SHALL start no new dispatch after closure. An uncertain external effect SHALL remain explicit.

#### Scenario: Child approval keeps the original requester

- **GIVEN** a child tool requires human approval
- **WHEN** the parent or an authorized requester resolves that approval
- **THEN** the decision applies only to that child call and its recorded requester
- **AND** a stale or unrelated decision grants no authority

#### Scenario: Recovery does not repeat an uncertain child action

- **GIVEN** an accepted child has an external action with no recorded terminal result
- **WHEN** the parent owner recovers
- **THEN** the system does not start the child again automatically
- **AND** the run reports an explicit non-success or uncertain state

#### Scenario: Status enforces run ownership

- **GIVEN** one requester owns a child run
- **AND** neither that requester nor the other requester is a reminder turn
- **WHEN** another requester asks for its status
- **THEN** the system denies that status request
- **AND** the owner can observe recorded progress and pending approval facts

#### Scenario: Accepted status is not terminal status

- **GIVEN** a child run has durable acceptance but no terminal result
- **WHEN** its owner requests status
- **THEN** the system reports a nonterminal state
- **AND** it does not report child completion

#### Scenario: Pending approval count is owner-only

- **GIVEN** an owned child run has one pending approval
- **WHEN** its owner requests status
- **THEN** the response includes a pending approval count of one
- **WHEN** that approval settles and the owner requests status again
- **THEN** the response includes a pending approval count of zero
- **AND** a non-owner cannot observe that count, unless the non-owner and the owner are turns of one session and one of the two is a reminder turn

#### Scenario: Stale approval does not grant authority

- **GIVEN** an approval refers to a stale run, call, requester, or attempt
- **WHEN** the system receives that approval
- **THEN** it rejects the approval as stale
- **AND** it dispatches no action under that approval

#### Scenario: Cancellation reports closure after local dispatch stops

- **GIVEN** an owner requests cancellation of an active child run
- **WHEN** the system persists cancellation intent
- **THEN** it acknowledges the intent without reporting terminal cancellation
- **WHEN** the local dispatch confirms closure
- **THEN** the system can report terminal cancellation
- **AND** it starts no new child dispatch after closure
- **AND** an uncertain external effect remains explicit

## ADDED Requirements

### Requirement: A reminder turn shares child runs with the other turns of its own session

A reminder turn and any other turn of the same session SHALL check the status of each other's child runs, and SHALL cancel them. The other turn can be a person turn, or an automated turn such as the delivery turn of a background job. The system SHALL ignore the sender ID and the principal classification when one of the two sides is a reminder turn. The side that is a reminder turn is the requester, or the turn that started the run. The system SHALL still require the same session, the same audience, the same trust boundary, and the same channel type as the original context of the run.

A reminder turn is a turn whose recorded principal classification is `VerifiedAutomation`, whose recorded source kind is `reminder`, and whose recorded transport authenticity is `LocalProcess`. The reminder path sets all three. A turn that lacks one of the three is not a reminder turn. A webhook turn has `VerifiedAutomation`, and the webhook sender can set its source kind to `reminder` through the event header or the route name. The webhook path sets the transport authenticity `Verified`, so a webhook turn is not a reminder turn. The rule for two turns that are not reminder turns SHALL NOT change. Such a requester owns a run only when all six requester facts match: session, audience, trust boundary, channel type, sender ID, and principal classification.

A reminder turn SHALL NOT see or control a child run of another session. No turn SHALL see or control a child run that a reminder turn started in another session. A reminder fire that delivers to another session or to a channel runs in a session that has no relation to the run. It owns no run of the person's session.

In a shared channel session, a turn of any sender can control a child run that a reminder turn started. A reminder turn can control a child run that any person started in the session. The owner accepted this effect. The session is the unit of trust for a reminder.

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

#### Scenario: A background job turn controls a child run that a reminder turn started

- **GIVEN** a reminder turn in a session started a child run
- **WHEN** the delivery turn of a background job in the same session, with the same audience, boundary, and channel type, asks for the status of the run or asks to cancel it
- **THEN** the system returns the status and persists the cancellation intent

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

#### Scenario: A webhook turn with the reminder source kind gets no relaxation

- **GIVEN** an operator turn in a session started a child run
- **AND** a webhook turn in the same session has principal `VerifiedAutomation`, source kind `reminder` from its event header, and transport authenticity `Verified`
- **WHEN** the webhook turn asks for the status of the run or asks to cancel it
- **THEN** the system denies both requests with the not-available error
- **AND** a person turn of another sender is also denied for a run that such a webhook turn started

#### Scenario: Automation that is not a reminder gets no relaxation

- **GIVEN** an operator turn in a session started a child run
- **WHEN** a turn with principal `VerifiedAutomation` and a source kind other than `reminder` asks for the status of the run
- **THEN** the system denies the request with the not-available error
- **AND** a person turn of another sender is also denied for a run that such a turn started
