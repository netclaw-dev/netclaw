## ADDED Requirements

### Requirement: Warm restart recovery for previously active sessions

The daemon SHALL persist the set of sessions that were active when a
config-triggered restart began and SHALL warm that set during startup after the
actor system is available. Warmed sessions SHALL recover through the normal
journal/snapshot path without requiring an immediate client-driven `EnsureSession`
call.

#### Scenario: Previously active session warms during startup recovery

- **GIVEN** a session was recorded as active in the restart manifest before shutdown
- **WHEN** the daemon starts again after the config-triggered restart
- **THEN** startup recovery re-creates that session through the session manager
- **AND** the session rehydrates from persisted state before normal traffic resumes

#### Scenario: Previously inactive session stays cold

- **GIVEN** a session was inactive when restart drain began
- **WHEN** the daemon starts again after the config-triggered restart
- **THEN** startup recovery does NOT proactively re-create that session
- **AND** the session remains lazily recoverable on its next normal resume or input

#### Scenario: Next turn receives restart continuity notice

- **GIVEN** a session was warmed from the restart manifest
- **WHEN** the next user turn begins after the daemon restart
- **THEN** the session injects a transient restart continuity notice into the turn context
- **AND** the notice explains that recovery resumed from the last durable checkpoint

### Requirement: Outstanding tool approvals restored on session recovery

When a session recovers, it SHALL restore outstanding tool approvals from
journaled tool-batch and approval events. Snapshots SHALL remain a cache of
state already implied by earlier journal events; they SHALL NOT be the source of
truth for in-flight approval state.

#### Scenario: Pending approval restored from journal on recovery

- **GIVEN** a `ToolApprovalRequested` event was written while a tool-approval prompt was outstanding
- **WHEN** the session cold-recovers through the journal/snapshot path
- **THEN** recovery SHALL restore the pending tool interaction from the journal
- **AND** the session SHALL log the count of recovered pending interactions

#### Scenario: Restored pending approval superseded by resolution events

- **GIVEN** a journal carrying a pending tool interaction
- **WHEN** a `ToolApprovalResolved`, `ToolBatchAbandoned`, `TurnRecorded`, or `SessionCompacted` event replays afterward
- **THEN** recovery SHALL clear the superseded pending interaction
- **AND** the recovered session SHALL NOT treat the superseded approval as outstanding

#### Scenario: Approval click resumes a cold-resumed session

- **GIVEN** a tool approval prompt was outstanding when the session passivated
- **WHEN** the session cold-resumes and the user clicks the approval afterward
- **THEN** the session SHALL re-drive the parked tool batch and continue the turn
- **AND** the user SHALL NOT have to send a separate message to wake the agent

#### Scenario: Pre-change snapshot recovers with no pending approval projection

- **GIVEN** a snapshot written before approval recovery existed
- **WHEN** the session recovers from that snapshot
- **THEN** recovery SHALL succeed with an empty pending-interaction set
- **AND** SHALL NOT fail or error on the missing field
