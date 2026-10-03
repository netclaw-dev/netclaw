## MODIFIED Requirements

### Requirement: TUI session browser

The system SHALL provide a Termina full-screen list of recent sessions from the
catalog. The operator SHALL be able to select a session for an inline chat
launch.

#### Scenario: Open session browser

- **WHEN** operator runs `netclaw sessions`
- **THEN** the full-screen TUI displays a list of recent sessions
- **AND** each entry shows title (or "Untitled"), channel type, turn count, and
  relative last activity time

#### Scenario: Select session to resume

- **GIVEN** the session browser is displayed with entries
- **WHEN** the user selects a session and confirms
- **THEN** the session browser exits and restores the primary terminal buffer
- **AND** the CLI starts inline chat with the selected session ID
- **AND** the chat client attaches through `EnsureSession`

#### Scenario: No sessions available

- **GIVEN** the session catalog is empty
- **WHEN** the session browser loads
- **THEN** the TUI displays an empty state message
- **AND** offers to exit and start a new inline chat session

## ADDED Requirements

### Requirement: Session resume restores structured settled events

The resume contract SHALL provide a chronological structured representation for
settled user, assistant, thought, tool, sub-agent, file, error, usage,
compaction, approval-outcome, and turn-outcome events that remain available.

The representation SHALL preserve stable event identities and all
security-permitted detail required by the chat Inspector. It SHALL NOT restore
an old settled event as an active Live Deck row.

#### Scenario: Resume a tool-rich session

- **GIVEN** a stored session contains one user turn, two parallel tool calls, a
  sub-agent run, one file, and an assistant response
- **WHEN** inline chat resumes that session
- **THEN** the client receives settled structured events in their original order
- **AND** the two tools retain distinct `CallId` values
- **AND** the sub-agent retains its `RunId` and parent `CallId`

#### Scenario: Resume does not create false active state

- **GIVEN** a prior session contains completed thought and tool records
- **WHEN** the client resumes the session
- **THEN** every recovered record uses a settled lifecycle state
- **AND** the Live Deck starts empty unless the daemon reports current live work

### Requirement: Legacy resume data has an explicit conversion path

The daemon SHALL convert supported legacy role-and-content history into the
canonical settled Turn representation. Invalid or unsupported legacy data SHALL
produce a visible resume error. The client SHALL NOT silently drop the invalid
record or create an empty transcript.

#### Scenario: Resume supported legacy history

- **GIVEN** a session uses the previous role-and-content history shape
- **WHEN** the client resumes that session
- **THEN** the daemon converts each supported message into a settled Turn event
- **AND** the client shows the recovered content in chronological order

#### Scenario: Resume unsupported legacy history

- **GIVEN** a legacy history record cannot convert without data ambiguity
- **WHEN** the client resumes that session
- **THEN** the daemon reports the record and conversion failure
- **AND** the client does not present an empty or partially silent transcript
