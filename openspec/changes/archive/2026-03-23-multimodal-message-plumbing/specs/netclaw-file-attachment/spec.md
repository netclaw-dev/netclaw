## ADDED Requirements

### Requirement: attach_file first-party tool

The system SHALL provide an `attach_file` first-party tool that the agent
calls to explicitly share a file with the user. The tool SHALL validate the
file path, read metadata, and emit a `FileOutput` event through the session
broadcast.

#### Scenario: Agent attaches a screenshot

- **GIVEN** a tool has written a file to the session directory
- **AND** the agent sees the file path in the tool result text
- **WHEN** the agent calls `attach_file` with the file path
- **THEN** the tool SHALL validate the path is within the session directory
- **AND** emit a `FileOutput` event with the file path, name, and MIME type
- **AND** return a text confirmation to the agent (e.g., "Attached: screenshot.png")

#### Scenario: Path traversal rejected

- **GIVEN** the agent calls `attach_file` with a path outside the session
  directory (e.g., `/etc/passwd`)
- **WHEN** the tool validates the path
- **THEN** the tool SHALL return an error message to the agent
- **AND** SHALL NOT emit a `FileOutput` event
- **AND** SHALL NOT read the file

#### Scenario: File not found

- **GIVEN** the agent calls `attach_file` with a path that does not exist
- **WHEN** the tool validates the path
- **THEN** the tool SHALL return an error message to the agent indicating
  the file was not found

### Requirement: Channel-specific file rendering

Channel adapters SHALL render `FileOutput` events according to their
capabilities. The agent SHALL NOT need to know which channel is active.

#### Scenario: Slack uploads file to thread

- **GIVEN** the Slack adapter receives a `FileOutput` event
- **WHEN** rendering the output for the Slack thread
- **THEN** the adapter SHALL call `files.uploadV2` to attach the file to the
  thread
- **AND** use the bot token for authentication

#### Scenario: TUI prints file path

- **GIVEN** the TUI adapter receives a `FileOutput` event
- **WHEN** rendering the output for the terminal
- **THEN** the adapter SHALL print the local file path for the user
