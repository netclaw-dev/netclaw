## ADDED Requirements

### Requirement: Config command surface

The CLI SHALL expose `netclaw config` as a top-level command. The
command SHALL be offline (no daemon connection), SHALL operate on
local config files only, and SHALL behave per the
`netclaw-config-command` capability. `netclaw config --help` SHALL
print a one-paragraph description and exit zero. `netclaw config show`
and `netclaw config validate` are RESERVED subcommands (PRD-004) and
SHALL print a not-yet-implemented notice and exit non-zero in this
change, preserving the documented future surface. Unknown subcommands
SHALL print usage and exit non-zero.

#### Scenario: Help text describes the command

- **WHEN** the operator runs `netclaw config --help`
- **THEN** the command exits with status 0
- **AND** stdout contains a one-paragraph description naming
  "interactive configuration editor"
- **AND** stdout references the `netclaw init` companion command
- **AND** stdout lists the reserved `show` and `validate` subcommands
  with a "not yet implemented; see PRD-004" note

#### Scenario: Reserved subcommand show exits non-zero with reservation notice

- **WHEN** the operator runs `netclaw config show`
- **THEN** stderr contains
  `\`netclaw config show\` is reserved for future use (PRD-004) and is
   not yet implemented.`
- **AND** the command exits with non-zero status
- **AND** no `netclaw.json` write occurs

#### Scenario: Reserved subcommand validate exits non-zero with reservation notice

- **WHEN** the operator runs `netclaw config validate`
- **THEN** stderr contains
  `\`netclaw config validate\` is reserved for future use (PRD-004)
   and is not yet implemented.`
- **AND** the command exits with non-zero status
- **AND** no `netclaw.json` write occurs

#### Scenario: Unknown subcommand rejected with usage

- **WHEN** the operator runs `netclaw config foo`
- **THEN** the command exits with non-zero status
- **AND** stderr contains usage text naming the dashboard launch
  (`netclaw config` with no args) and the reserved subcommands

#### Scenario: No-args invocation launches dashboard

- **WHEN** the operator runs `netclaw config` with no arguments
- **AND** `netclaw.json` exists
- **THEN** the dashboard launches per the
  `netclaw-config-command` capability
