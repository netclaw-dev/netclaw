# session-resume Specification

## Purpose

Define session browsing, selection, and resumption behavior across TUI and CLI
entry points. Covers the daemon-side join path, client API surface, and TUI
session browser.
## Requirements
### Requirement: Session listing via REST API

The system SHALL expose session catalog data through the existing
`GET /api/sessions` REST endpoint. The `DaemonClient` SHALL query this endpoint
to retrieve recent sessions for display in the TUI or CLI.

#### Scenario: List recent sessions

- **WHEN** the client calls `ListSessionsAsync()`
- **THEN** a GET request is made to `/api/sessions`
- **AND** the response contains session entries with persistence ID, channel,
  title, turn count, last activity timestamp, and log path

#### Scenario: Daemon unreachable

- **WHEN** the client calls `ListSessionsAsync()` and the daemon is not running
- **THEN** the method throws or returns an empty list with a connection error
- **AND** no crash occurs in the TUI

### Requirement: Session resume via SignalR

The system SHALL allow a SignalR client to resume an existing session by passing
its session ID to `EnsureSession`. The daemon SHALL materialize a new
`SessionPipeline` against the provided session ID, triggering actor rehydration
from the journal if the session is passivated.

#### Scenario: Resume a passivated session

- **GIVEN** a session with ID `C07ABC/1234567890.123456` was previously active
  and has passivated
- **WHEN** a SignalR client calls `EnsureSession` with that session ID
- **THEN** the daemon materializes a `SessionPipeline` for that ID
- **AND** the session actor rehydrates from the Akka journal
- **AND** the client receives output from subsequent turns

#### Scenario: Resume a live session

- **GIVEN** a session is currently active with a Slack subscriber
- **WHEN** a SignalR client calls `EnsureSession` with that session ID
- **THEN** the daemon materializes a new `SessionPipeline` as an additional
  subscriber
- **AND** both the Slack and SignalR subscribers receive output independently

#### Scenario: Resume with invalid session ID

- **WHEN** a SignalR client calls `EnsureSession` with a session ID that does
  not exist in the catalog or journal
- **THEN** a new session is created with that ID
- **AND** the client can begin a fresh conversation

### Requirement: TUI session browser

The system SHALL provide a Terminal.Gui list view displaying recent sessions
from the catalog. The user SHALL be able to select a session to resume it in
the chat page.

#### Scenario: Open session browser

- **WHEN** operator runs `netclaw sessions`
- **THEN** the TUI displays a list of recent sessions
- **AND** each entry shows title (or "Untitled"), channel type, turn count, and
  relative last activity time

#### Scenario: Select session to resume

- **GIVEN** the session browser is displayed with entries
- **WHEN** the user selects a session and confirms
- **THEN** the TUI navigates to the chat page
- **AND** the chat page attaches to the selected session ID via `EnsureSession`

#### Scenario: No sessions available

- **GIVEN** the session catalog is empty
- **WHEN** the session browser loads
- **THEN** the TUI displays an empty state message
- **AND** offers to start a new chat session

### Requirement: CLI direct resume

The system SHALL support `netclaw chat --resume <session-id>` to skip the
session browser and open the chat page directly attached to the specified
session.

#### Scenario: Resume by ID

- **WHEN** operator runs `netclaw chat --resume C07ABC/1234567890.123456`
- **THEN** the chat page opens attached to the specified session
- **AND** the session actor rehydrates if passivated

#### Scenario: Resume with unknown ID

- **WHEN** operator runs `netclaw chat --resume nonexistent-id`
- **THEN** a new session is created with that ID
- **AND** the chat page opens with an empty conversation

### Requirement: Resumed session indicator

The system SHALL display a visual indicator when the chat page is attached to
a resumed session rather than a freshly created one.

#### Scenario: Show resumed session context

- **GIVEN** the user resumed a session with 5 prior turns and a title
- **WHEN** the chat page loads
- **THEN** a status message displays "Resumed: {title} (5 turns)"
- **AND** subsequent user input continues the conversation from the recovered
  state

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

### Requirement: Recovered pending approvals restore turn context

When a session recovers pending tool approvals from the journal, it SHALL also restore the original turn context for each pending approval. The restored context SHALL include the requester, audience, boundary, channel type, approval capability, principal classification, provenance, and adopted-context safety state needed to resume the original request faithfully.

#### Scenario: Pending approval restores original context

- **GIVEN** a `ToolApprovalRequested` event was written with turn context while a prompt was outstanding
- **WHEN** the session cold-recovers through the journal path
- **THEN** the pending approval is restored with that turn context
- **AND** approval redrive uses the restored context rather than deriving a new context from the session id

#### Scenario: Legacy approval event uses compatibility restoration

- **GIVEN** a pre-change `ToolApprovalRequested` event has no turn-context record but still has legacy persisted trust fields
- **WHEN** the session recovers the event
- **THEN** the session MAY construct turn context from those legacy fields
- **AND** this compatibility path is isolated from the normal new-event path

#### Scenario: Incomplete legacy approval fails loud

- **GIVEN** a recovered pending approval lacks enough persisted context to restore the original requester and trust context
- **WHEN** an approval response arrives for that pending call
- **THEN** the session does not redrive the tool under a synthesized permissive context
- **AND** the user receives an explicit expired or unrecoverable approval notice

### Requirement: Restarted approvals resume as the original request

An approval response received after idle passivation, cold recovery, or daemon restart SHALL resume the same original session turn. The resumed tool batch and any continuation LLM/tool calls SHALL use the restored turn context until the resumed turn completes or is abandoned.

#### Scenario: Approval after cold recovery resumes original request

- **GIVEN** a session has recovered a pending approval from the journal
- **WHEN** the original requester approves the prompt
- **THEN** the session re-drives the parked tool batch
- **AND** the tool execution context uses the original turn audience, boundary, channel type, and approval capability

#### Scenario: Continuation after redrive keeps restored context

- **GIVEN** a recovered approval redrive has completed its parked tool call
- **WHEN** the follow-up LLM response asks for another tool call in the same turn
- **THEN** the continuation tool call uses the same restored turn context
- **AND** it does not fall back to a context derived from missing transport metadata

