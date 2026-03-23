## ADDED Requirements

### Requirement: attach_file first-party tool

The system SHALL provide an `attach_file` first-party tool that allows the
agent to explicitly share files from the session directory with the user. The
tool SHALL validate file paths for security, read file metadata, and emit a
`FileOutput` event through the session broadcast.

#### Scenario: Agent attaches a file

- **GIVEN** a file exists in the session directory
- **WHEN** the agent calls `attach_file` with the file path
- **THEN** the tool SHALL validate the path is within the session directory
- **AND** emit a `FileOutput` event with the file path, name, and MIME type
- **AND** return a text confirmation to the agent

#### Scenario: Path traversal rejected

- **GIVEN** the agent calls `attach_file` with a path outside the session
  directory (e.g., `/etc/passwd` or `../../sensitive-file`)
- **WHEN** the tool validates the path
- **THEN** the tool SHALL return an error message to the agent
- **AND** SHALL NOT emit a `FileOutput` event
- **AND** SHALL NOT access the file

#### Scenario: File not found

- **GIVEN** the agent calls `attach_file` with a path that does not exist
  within the session directory
- **WHEN** the tool validates the path
- **THEN** the tool SHALL return an error message indicating the file was not
  found

#### Scenario: Tool registered at startup

- **WHEN** the Netclaw process starts
- **THEN** the `attach_file` tool SHALL be registered as an MEAI tool
  definition
- **AND** the tool definition SHALL include name, description, and parameter
  schema
