# netclaw-scheduling Delta Spec — reminder-execution-history

## ADDED Requirements

### Requirement: Execution history CLI command

The CLI SHALL provide a `netclaw reminder history <id>` subcommand that
reads and displays the execution history for a given reminder. The command
SHALL accept an optional `--last N` flag (default: 20) to limit the number
of records shown. Output SHALL be formatted as a table with columns:
`fired_at`, `status`, `duration`, `session_id`. If no history file exists
for the given ID, the command SHALL print a clear "no history recorded"
message and exit with code 0.

#### Scenario: History displayed for a reminder with records

- **WHEN** the operator runs `netclaw reminder history daily-summary`
- **THEN** the most recent 20 execution records are shown as a table
- **AND** each row includes fired_at (UTC), success/failure status,
  duration in ms, and the session ID

#### Scenario: Limit applied with --last flag

- **WHEN** the operator runs `netclaw reminder history daily-summary --last 5`
- **THEN** only the 5 most recent records are shown

#### Scenario: No history file returns graceful message

- **WHEN** the operator runs `netclaw reminder history new-reminder`
  and no history file exists for `new-reminder`
- **THEN** the command prints "No execution history recorded for new-reminder"
- **AND** exits with code 0

#### Scenario: Unknown reminder ID returns error

- **WHEN** the operator runs `netclaw reminder history nonexistent-id`
  and no reminder definition exists for that ID
- **THEN** the command exits with a non-zero code and a clear error message

### Requirement: get_reminder_history agent tool

The system SHALL provide a `get_reminder_history` tool requiring the
`scheduling` grant. The tool SHALL accept a `reminder_id` parameter and an
optional `last` parameter (default: 20, max: 100). The tool SHALL return a
structured list of execution records enabling the agent to assess job health
inline. If no history exists, the tool SHALL return an empty list.

#### Scenario: Agent queries recent executions

- **GIVEN** the agent holds the `scheduling` grant
- **WHEN** the agent calls `get_reminder_history` with `reminder_id: "daily-summary"`
- **THEN** the tool returns up to 20 recent execution records
- **AND** each record includes firedAt, success, durationMs, sessionId,
  and errorMessage

#### Scenario: Agent enforces max record count

- **WHEN** the agent calls `get_reminder_history` with `last: 200`
- **THEN** the tool returns at most 100 records

#### Scenario: Tool rejected without scheduling grant

- **GIVEN** the current session does not hold the `scheduling` grant
- **WHEN** the agent attempts to call `get_reminder_history`
- **THEN** the tool call is rejected by the ACL policy
- **AND** the agent receives a permission-denied response
