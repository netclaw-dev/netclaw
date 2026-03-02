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
