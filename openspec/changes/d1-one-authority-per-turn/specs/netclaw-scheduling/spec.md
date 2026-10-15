## ADDED Requirements

### Requirement: A reminder that waits behind an approval is queued, not failed

A reminder fire for a session that has an open approval SHALL NOT abandon the
approval. The session SHALL store the reminder as an accepted input and
acknowledge it. The reminder SHALL then wait and run once as its own turn after
the approval closes.

- The acknowledgment SHALL follow the journal record of the input, as the
  "Accepted input survives a graceful stop" requirement of `session-resume`
  states.
- A waiting reminder SHALL keep its reminder identity, also after a rebuild
  from the journal. A redelivery of the same occurrence SHALL NOT add a second
  waiting input.
- A wait behind an approval SHALL NOT be a delivery failure by itself. This
  change does not change the delivery observation timeout. The reminder
  delivery backstop for a long wait is out of scope (see `design.md`).

#### Scenario: A reminder fires while an approval is open

- **GIVEN** a `CurrentSession` reminder targets a session with a parked approval
- **WHEN** the reminder fires
- **THEN** the session stores the input and returns `CommandAck`
- **AND** the parked approval stays open
- **AND** the reminder runs once after the approval closes

#### Scenario: Redelivery of a waiting reminder is not a second input

- **GIVEN** a reminder waits behind an open approval
- **AND** the daemon restarted and the session rebuilt the waiting reminder
- **WHEN** Akka.Reminders redelivers the same occurrence
- **THEN** the session acknowledges it
- **AND** it adds no second waiting input

#### Scenario: A reminder never abandons the approval

- **GIVEN** a parked approval that no one answered
- **WHEN** a reminder fires
- **THEN** the approval prompt stays open
- **AND** the tool call stays unrun
