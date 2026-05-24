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

When recovery restores an outstanding approval for a channel that supports
interactive prompt updates, restart continuity SHALL include prompt
reconciliation continuity as well as session-state continuity. A post-recovery
approval click on the original prompt SHALL both resume or close the approval
workflow in the session and drive reconciliation of the original prompt's
visible state when a durable prompt handle exists.

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

#### Scenario: Approval click resumes a cold-resumed session and reconciles prompt continuity

- **GIVEN** a tool approval prompt was outstanding when the session passivated
- **WHEN** the session cold-resumes and the user clicks the approval afterward
- **THEN** the session SHALL re-drive the parked tool batch and continue the turn
- **AND** the original prompt SHALL be reconciled into a terminal state when the channel has a durable prompt handle

#### Scenario: Expired recovered prompt remains user-visible as expired rather than silently stale

- **GIVEN** a recovered session receives a response for an approval prompt that is no longer pending or reconstructable
- **WHEN** the session classifies that prompt as expired
- **THEN** the user SHALL receive an explicit expired-prompt notice
- **AND** any channel with a durable handle for the original prompt SHALL reconcile it into an expired or disabled state

#### Scenario: Pre-change recovery still succeeds without prompt-handle durability

- **GIVEN** a session snapshot or restart path created before prompt-handle reconciliation existed
- **WHEN** the session recovers from that older durable state
- **THEN** recovery SHALL still succeed with normal pending-approval restoration semantics
- **AND** missing prompt-handle metadata SHALL degrade to explicit no-reconciliation diagnostics rather than a recovery failure
