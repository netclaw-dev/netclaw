## ADDED Requirements

### Requirement: CLI command for immediate reminder execution

The CLI SHALL provide a `netclaw reminder run <id>` subcommand that asks the
running daemon to trigger immediate execution of an existing reminder. The
subcommand SHALL require a daemon connection, SHALL call the daemon reminder run
endpoint, and SHALL print a clear success or failure message.

The command SHALL NOT edit reminder files directly and SHALL NOT attempt to run a
reminder when the daemon is offline.

#### Scenario: Operator runs reminder immediately

- **GIVEN** the daemon is running
- **WHEN** the operator runs `netclaw reminder run daily-summary`
- **THEN** the CLI calls `POST /api/reminders/daily-summary/run`
- **AND** exits with code `0` when the daemon accepts the run
- **AND** prints a message naming the reminder and indicating the run started

#### Scenario: Daemon-required failure is clear

- **GIVEN** no daemon API is available to the CLI
- **WHEN** the operator runs `netclaw reminder run daily-summary`
- **THEN** the CLI exits non-zero
- **AND** prints that `reminder run` requires a running daemon

#### Scenario: Daemon rejection is surfaced

- **GIVEN** the daemon rejects immediate execution because the reminder is disabled, busy, missing, or scheduling is disabled
- **WHEN** the operator runs `netclaw reminder run daily-summary`
- **THEN** the CLI exits non-zero
- **AND** prints the daemon-provided reason without claiming the run started
