## ADDED Requirements

### Requirement: A session with only a child run gets a restart reminder

When the daemon drains a session that has no pending input but owes a child result (a running child, or an ended child with no notice), the drain SHALL return a restart reminder. The reminder SHALL use the recorded context of the oldest such child. After the restart, the reminder wakes the session, the recovery ends the lost child, and one notice reports it.

#### Scenario: A child-only session is woken

- **GIVEN** a session with a running child and no pending input
- **WHEN** the daemon restarts
- **THEN** the drain returns a reminder with the context of the child
- **AND** after the restart one notice reports the lost child
