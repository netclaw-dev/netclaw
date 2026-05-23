## ADDED Requirements

### Requirement: Config command surface

The CLI SHALL expose `netclaw config` as a top-level command. The
command SHALL be offline (no daemon connection), SHALL operate on
local config files only, and SHALL behave per the
`netclaw-config-command` capability. `netclaw config --help` SHALL
print a one-paragraph description and exit zero. Invocations with any
positional argument SHALL print usage and exit non-zero in this change
(subcommands such as `netclaw config show|validate` remain reserved
for future work and SHALL NOT execute as a side effect).

#### Scenario: Help text describes the command

- **WHEN** the operator runs `netclaw config --help`
- **THEN** the command exits with status 0
- **AND** stdout contains a one-paragraph description naming
  "interactive configuration editor"
- **AND** stdout references the `netclaw init` companion command

#### Scenario: Unknown subcommand rejected

- **WHEN** the operator runs `netclaw config foo`
- **THEN** the command exits with non-zero status
- **AND** stderr contains usage text

#### Scenario: No-args invocation launches dashboard

- **WHEN** the operator runs `netclaw config` with no arguments
- **AND** `netclaw.json` exists
- **THEN** the dashboard launches per the
  `netclaw-config-command` capability
