## ADDED Requirements

### Requirement: Accepted input survives a graceful stop

The session SHALL record each accepted input before it acknowledges that input. The record SHALL retain a stable input ID, source ID, order, text, media, authority, and delivery context. A failed journal write SHALL reject the input.

Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.

#### Scenario: Input ack follows its journal record

- **GIVEN** a user sends input to a ready session or a busy session
- **WHEN** the journal confirms the accepted input record
- **THEN** the session acknowledges the input
- **AND** cold recovery restores its text, media, order, and original authority

#### Scenario: Failed journal write rejects input

- **GIVEN** the journal cannot store an input record
- **WHEN** the user sends that input
- **THEN** the session rejects the input with a visible error
- **AND** the session does not start a model call for it

#### Scenario: Source retry does not duplicate accepted input

- **GIVEN** the journal stores an input with a stable source message ID
- **WHEN** the source retries that same message after it loses the ack
- **THEN** the session acknowledges the existing input
- **AND** the session does not add a second copy to the queue

### Requirement: Graceful stop records only safe resume candidates

The daemon SHALL record a resume candidate after any graceful stop only for confirmed interrupted model work or accepted queued input. The candidate SHALL have one absolute deadline ten minutes after interruption. A stopped model task SHALL be confirmed before the session acknowledges drain.

#### Scenario: Interrupted model call creates a candidate

- **GIVEN** an admitted turn has a live model call and no tool batch has started in that turn
- **WHEN** a graceful stop interrupts the call after a short completion grace
- **THEN** the session waits for the model task to stop
- **AND** the daemon records a candidate for that original turn

#### Scenario: Completed reply with queued input creates a candidate

- **GIVEN** a turn finishes during drain and accepted queued input remains
- **WHEN** the session acknowledges drain
- **THEN** the daemon records a candidate for the accepted queue

#### Scenario: Completed reply without queued input stays quiet

- **GIVEN** a turn finishes during drain and no accepted queued input remains
- **WHEN** the daemon starts again
- **THEN** the daemon starts no model call for that session

#### Scenario: Tool effect or partial reply blocks automatic resume

- **GIVEN** the interrupted turn has a tool batch or user-visible partial text
- **WHEN** the daemon stops
- **THEN** the daemon does not record an executable model resume candidate
- **AND** it reports the blocked work to the operator

### Requirement: Eligible restart resumes original work once

The daemon SHALL resume an eligible candidate only before its deadline and after its output route is ready. The session SHALL use the recorded authority and input IDs. It SHALL not create a new reminder turn or ask the user for stored context.

#### Scenario: Cold recovery resumes the original turn

- **GIVEN** a graceful stop confirmed model cancellation and stored the admitted input
- **WHEN** the daemon starts before the candidate deadline
- **THEN** the session resumes one model call for the original turn
- **AND** it uses the recorded requester and trust boundary

#### Scenario: Accepted queue follows the original turn

- **GIVEN** a canceled turn and multiple accepted queued messages
- **WHEN** the resumed original turn finishes
- **THEN** the session sends the queued messages in their original order
- **AND** it uses one follow-up model call for that queue

#### Scenario: Expired candidate stays quiet

- **GIVEN** the absolute candidate deadline has passed
- **WHEN** the daemon starts or tries to resume the session
- **THEN** it starts no model call from that candidate
- **AND** it reports and removes the expired candidate once

#### Scenario: Output route is absent

- **GIVEN** a candidate has no live route that can deliver the resumed output
- **WHEN** the daemon starts before its deadline
- **THEN** it does not run a model call yet
- **AND** it reports the blocked candidate to the operator

#### Scenario: Newer work supersedes a candidate

- **GIVEN** a newer user turn starts before the recovery request reaches the session
- **WHEN** the older recovery request arrives
- **THEN** the session rejects the stale request and starts no duplicate call

#### Scenario: Incompatible queued authority blocks automatic resume

- **GIVEN** accepted queued messages have incompatible trust boundaries
- **WHEN** the daemon tries to resume that queue
- **THEN** the session keeps the messages in the journal
- **AND** it reports the blocked queue instead of widening tool authority
