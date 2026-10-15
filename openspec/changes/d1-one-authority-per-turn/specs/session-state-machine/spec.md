## ADDED Requirements

### Requirement: Waiting input and idle passivation

The idle timeout SHALL close a half-answered approval batch and then run any
waiting input before the session passivates.

- When the idle timeout closes a batch that has some answered calls and some
  open calls, and acknowledged input waits, the session SHALL abandon the whole
  batch. It SHALL run the waiting input first, under its own recorded
  authority. It SHALL then passivate after that work ends.
- The deferred passivation SHALL apply the same active-work check as the normal
  idle path. A session with a live background job SHALL stay in memory. The
  re-armed idle timeout SHALL decide again.
- The deferred passivation SHALL be a single decision. A later turn end SHALL
  NOT passivate the session on its own.
- When the batch has no waiting input, idle behavior SHALL stay as the "Idle
  passivation follows active work" requirement states.
- An input that waits behind an open approval SHALL NOT keep a session in
  memory. The session MAY passivate in `Ready` while that input waits.

#### Scenario: Idle abandonment runs the waiting input first

- **GIVEN** a batch with one answered call and one open call
- **AND** an acknowledged reminder waits
- **WHEN** the idle timeout fires
- **THEN** the session abandons the batch
- **AND** it runs the reminder once
- **AND** it passivates after the reminder turn ends

#### Scenario: A job that the waiting input starts keeps the session in memory

- **GIVEN** the idle timeout closed a half-answered batch and a reminder runs
- **AND** the reminder turn starts a background job
- **WHEN** the reminder turn ends
- **THEN** the session does not passivate
- **AND** it does not stop the job
- **WHEN** the job ends and the idle timeout fires
- **THEN** the session passivates

#### Scenario: No job means the session passivates

- **GIVEN** the idle timeout closed a half-answered batch and a reminder runs
- **AND** the reminder turn starts no background job
- **WHEN** the reminder turn ends
- **THEN** the session passivates

#### Scenario: A session passivates while an input waits behind an approval

- **GIVEN** a session in `Ready` with a parked approval and a waiting reminder
- **AND** no active work remains
- **WHEN** the one-hour idle timeout fires
- **THEN** the session enters `Passivating`
- **AND** the reminder stays in the journal as a pending input

#### Scenario: Empty buffer keeps the existing idle behavior

- **GIVEN** the idle timeout closes a half-answered batch
- **AND** no input waits
- **WHEN** the abandonment completes
- **THEN** the session passivates at once
