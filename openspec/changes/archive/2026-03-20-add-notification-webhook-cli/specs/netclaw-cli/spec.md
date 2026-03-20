## ADDED Requirements

### Requirement: Notification webhook management command surface
The CLI SHALL provide a plain-console `netclaw notification webhook` command
group for offline notification webhook management. The command group SHALL
support `list`, `add`, `remove`, and `test` subcommands, SHALL emit
automation-friendly exit codes, and SHALL provide remediation-first error output
for invalid selectors, invalid configuration, and probe failures.

#### Scenario: Help text shows notification webhook subcommands
- **WHEN** the operator runs `netclaw notification webhook --help`
- **THEN** the CLI prints usage for `list`, `add`, `remove`, and `test`
- **AND** the help text identifies the command group as plain CLI, not TUI

#### Scenario: Invalid subcommand returns usage error
- **WHEN** the operator runs `netclaw notification webhook rotate`
- **THEN** the CLI prints a usage error for the unsupported subcommand
- **AND** the command exits with a usage-error status code

### Requirement: Notification webhook commands preserve secret redaction
Operator-facing output from notification webhook commands SHALL NOT print static
header values or other secret-bearing notification fields. When reporting target
details, the CLI SHALL show only safe identity data such as target index, name,
redacted URL identity, configured header names, and validation field paths.

#### Scenario: Add command does not echo secret header value
- **WHEN** the operator runs `netclaw notification webhook add` with `--header "Authorization: Bearer secret-token"`
- **THEN** command output may mention the `Authorization` header name
- **AND** command output does not include `secret-token`

#### Scenario: Probe failure output remains redacted
- **WHEN** the operator runs `netclaw notification webhook test` for a target with static headers and the request fails
- **THEN** the CLI reports the failure using target identity and safe diagnostics
- **AND** no configured header value appears in the output

#### Scenario: List output does not reveal full webhook URL
- **WHEN** the operator runs `netclaw notification webhook list` for a target
  whose webhook URL path contains a secret token
- **THEN** command output shows only redacted URL identity for that target
- **AND** the full webhook path does not appear in output
