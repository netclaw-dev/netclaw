## ADDED Requirements

### Requirement: Outstanding tool approvals restored on session recovery

When a session recovers from its snapshot, it SHALL restore the set of pending
tool interactions that were outstanding when the snapshot was written, so an
approval prompt issued before passivation or restart is honored after recovery.
Restored pending interactions SHALL be discarded when a later journal event
(`TurnRecorded` or `SessionCompacted`) replays over the snapshot, because such
an event means no tool batch is mid-flight.

#### Scenario: Pending approval restored from snapshot on recovery

- **GIVEN** a session snapshot was written while a tool-approval prompt was outstanding
- **WHEN** the session cold-recovers through the journal/snapshot path
- **THEN** recovery SHALL restore the pending tool interaction from the snapshot
- **AND** the session SHALL log the count of recovered pending interactions

#### Scenario: Restored pending approval superseded by later journal events

- **GIVEN** a snapshot carrying a pending tool interaction
- **WHEN** a `TurnRecorded` or `SessionCompacted` event replays after the snapshot
- **THEN** recovery SHALL clear the restored pending interactions
- **AND** the recovered session SHALL NOT treat the superseded approval as outstanding

#### Scenario: Approval click resumes a cold-resumed session

- **GIVEN** a tool approval prompt was outstanding when the session passivated
- **WHEN** the session cold-resumes and the user clicks the approval afterward
- **THEN** the session SHALL re-drive the parked tool batch and continue the turn
- **AND** the user SHALL NOT have to send a separate message to wake the agent

#### Scenario: Pre-change snapshot recovers with no pending approvals

- **GIVEN** a snapshot written before this capability existed (no persisted
  pending interactions)
- **WHEN** the session recovers from that snapshot
- **THEN** recovery SHALL succeed with an empty pending-interaction set
- **AND** SHALL NOT fail or error on the missing field
