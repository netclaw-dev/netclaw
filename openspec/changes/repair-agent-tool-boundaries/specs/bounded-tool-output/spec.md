## MODIFIED Requirements

### Requirement: Spill to a session-scoped file with a steer

When a result exceeds its inline budget, the dispatcher SHALL write the full redacted result to an internal file under the current session `tool-calls` directory. The dispatcher SHALL derive the file name from the sanitized call id. It SHALL return the opaque call id and a steer to `tool_output_read`. It SHALL NOT reveal the raw spill path or direct the model to shell, grep, or `file_read`. When no session directory or call id is available, the dispatcher SHALL return the inline window without a spill steer.

#### Scenario: Spill file stays internal

- **WHEN** a result over budget is produced in a session with a directory
- **THEN** the full redacted result is written under the session `tool-calls` directory
- **AND** the inline result includes the opaque call id
- **AND** the steer names `tool_output_read`
- **AND** the steer contains no filesystem path

#### Scenario: Spilled file is redacted

- **WHEN** a result that contains a secret is spilled
- **THEN** the internal spill file has the secret redacted
- **AND** redaction occurs before the spill write

#### Scenario: Call id cannot escape the spill directory

- **WHEN** the call id contains path-traversal characters
- **THEN** the spill file stays inside the tool-calls directory
- **AND** the dispatcher reveals no raw path
