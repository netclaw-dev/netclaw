## MODIFIED Requirements

### Requirement: Session listing via REST API
The system SHALL expose session catalog data through the existing
`GET /api/sessions` REST endpoint. The `DaemonClient` SHALL query this endpoint
to retrieve recent sessions for display in the TUI or CLI. Each entry SHALL
include session status using the `active` / `inactive` vocabulary, and
`last_activity` SHALL reflect real session output activity rather than resume or
deactivation bookkeeping.

#### Scenario: List recent sessions
- **WHEN** the client calls `ListSessionsAsync()`
- **THEN** a GET request is made to `/api/sessions`
- **AND** the response contains session entries with persistence ID, channel,
  title, status, turn count, last activity timestamp, and log path

#### Scenario: Daemon unreachable
- **WHEN** the client calls `ListSessionsAsync()` and the daemon is not running
- **THEN** the method throws or returns an empty list with a connection error
- **AND** no crash occurs in the TUI

#### Scenario: Resume reactivates a passivated session without rewriting activity time
- **GIVEN** a catalog entry exists for a previously passivated session
- **WHEN** a client reattaches to that session before any new turn completes
- **THEN** the catalog status becomes `active`
- **AND** the prior `last_activity` timestamp is preserved until new session output occurs
