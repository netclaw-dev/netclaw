## ADDED Requirements

### Requirement: Waiting input survives restart, passivation, and an actor restart

An acknowledged input that waits behind a parked approval SHALL survive a
daemon restart, an idle passivation, and a restart of the session actor by its
supervisor. Each such input SHALL reach the model once. Use the
[engineering glossary](../../../../docs/spec/GLOSSARY.md#requester) for the
term "requester".

- On recovery, when a parked approval remains, the session SHALL rebuild the
  waiting inputs from the pending `InputAdmitted` records. It SHALL restore the
  recorded authority and the source identity of each input, including a
  reminder identity.
- The rebuilt inputs SHALL wait in their recorded order. They SHALL run under
  the rule "One authority per turn for buffered input" after the approval
  closes.
- The rebuild SHALL add no new store. A pending input that a restart resume
  consumes SHALL leave the waiting queue.
- A rebuilt input SHALL NOT block a snapshot and SHALL NOT block a later restart
  resume.
- When a supervisor restarts the session actor, a copy of one input that
  recovery rebuilt and a copy that the old actor requeued SHALL count as one
  input.

#### Scenario: A waiting reminder survives a daemon restart

- **GIVEN** a parked approval and an acknowledged reminder that waits behind it
- **WHEN** the daemon restarts and the session recovers
- **AND** the requester approves and the batch finishes
- **THEN** the reminder runs once as its own turn
- **AND** no pending input remains

#### Scenario: A waiting reminder survives passivation

- **GIVEN** a parked approval and an acknowledged reminder that waits behind it
- **WHEN** the idle timeout passivates the session
- **AND** the requester later answers the approval
- **THEN** the session recovers from its journal
- **AND** the reminder runs once after the approval closes

#### Scenario: A later restart resume still works

- **GIVEN** a recovered waiting reminder ran after its approval closed
- **AND** a later turn ended with an interrupted input
- **WHEN** the daemon restarts again
- **THEN** the restart resume restores the interrupted input
- **AND** the reminder does not run again

#### Scenario: An actor restart does not run an input twice

- **GIVEN** a message from a person waits behind an open approval
- **WHEN** the supervisor restarts the session actor
- **THEN** the message reaches the model once after the approval closes

#### Scenario: A cold session without a parked approval rebuilds nothing

- **GIVEN** pending inputs exist and no parked approval remains
- **WHEN** the session recovers
- **THEN** it does not rebuild a waiting queue
- **AND** the existing restart resume rules decide what runs
