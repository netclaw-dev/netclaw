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

### Requirement: Graceful stop of durable approval waits

During a graceful daemon stop, a session SHALL finish drain when every unfinished tool call waits on a durable approval. The session SHALL wait for the tool task to stop before it acknowledges drain. The journal SHALL retain approval requests and completed tool results for cold recovery.

Use the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

#### Scenario: One durable approval stops promptly

- **GIVEN** a session has one unfinished tool call with a journaled approval request
- **WHEN** the daemon requests a graceful drain
- **THEN** the session stops the tool task and acknowledges drain without an approval response
- **AND** the original requester can approve the call after cold recovery
- **AND** the recovered turn keeps its original authority

#### Scenario: Completed sibling retains its result

- **GIVEN** one tool result has a journal record and another tool call waits on a journaled approval
- **WHEN** the daemon requests a graceful drain
- **THEN** the session acknowledges drain after the tool task stops
- **AND** recovery does not execute the completed sibling again

#### Scenario: Active sibling prevents the fast path

- **GIVEN** one tool call waits on a journaled approval and another tool call has no journaled result or approval
- **WHEN** the daemon requests a graceful drain
- **THEN** the session does not acknowledge drain through the approval fast path
- **AND** the current bounded drain path remains in effect

#### Scenario: Accepted buffered input prevents the fast path

- **GIVEN** a session has accepted user input in its actor buffer
- **WHEN** the daemon requests a graceful drain
- **THEN** the session does not acknowledge drain through the approval fast path

#### Scenario: Non-durable or resolved approval prevents the fast path

- **GIVEN** an unfinished call has a non-durable approval or a resolved approval without a result
- **WHEN** the daemon requests a graceful drain
- **THEN** the session does not acknowledge drain through the approval fast path

#### Scenario: Deferred approval response prevents the fast path

- **GIVEN** the session has a deferred approval response
- **WHEN** the daemon requests a graceful drain
- **THEN** the session does not acknowledge drain through the approval fast path

#### Scenario: Cancellation preserves the approval prompt

- **GIVEN** a durable approval prevents a tool call from execution
- **WHEN** graceful drain cancels the pipeline task
- **THEN** cancellation does not become a synthetic timeout or failed tool result
- **AND** the existing approval prompt remains unresolved
- **AND** its original button or text response resumes the recovered turn
- **AND** a duplicate response does not execute the tool again

### Requirement: Idle passivation proceeds with pending approvals

A session SHALL NOT defer idle passivation because tool approval prompts are
outstanding. Pending approval state is journaled (`ToolApprovalRequested` /
`ToolApprovalResolved`) and the approval response path already rehydrates a
passivated session and resumes the original turn, so keeping the session in
memory while a human decides adds no correctness — only resident memory.
Active live subscribers (CLI/TUI connections) SHALL continue to defer
passivation, because subscriber connections are ephemeral and cannot survive
actor stop. The existing resolved-approval abandonment behavior (a parked tool
batch whose approval was granted but whose tool result never completed) SHALL
be preserved.

#### Scenario: Session passivates with an approval prompt outstanding

- **GIVEN** a session is idle past its idle timeout
- **AND** a tool approval prompt is outstanding
- **AND** no live subscribers are attached
- **WHEN** the receive timeout fires
- **THEN** the session passivates normally
- **AND** the pending approval remains recoverable from the journal

#### Scenario: Approval click after passivation resumes the turn

- **GIVEN** a session passivated with an approval prompt outstanding
- **WHEN** the user responds to the approval prompt
- **THEN** the session rehydrates from the journal
- **AND** re-drives the parked tool batch per the existing restored-approval
  requirements

#### Scenario: Live subscribers still defer passivation

- **GIVEN** a session is idle past its idle timeout
- **AND** a live CLI or TUI subscriber is attached
- **WHEN** the receive timeout fires
- **THEN** passivation is deferred while the subscriber remains attached
