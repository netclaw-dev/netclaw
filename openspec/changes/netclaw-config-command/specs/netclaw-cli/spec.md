## ADDED Requirements

### Requirement: Config command surface

The CLI SHALL expose `netclaw config` as a top-level command. The command
SHALL operate on local config files and SHALL behave per the
`netclaw-config-command` capability.

If no config exists, `netclaw config` SHALL print a plain message directing
the operator to `netclaw init` and exit non-zero without launching Termina.

#### Scenario: Help text describes config as post-install settings surface

- **WHEN** the operator runs `netclaw config --help`
- **THEN** the command exits zero
- **AND** help text describes `netclaw config` as the main post-install
  settings surface
- **AND** help text references `netclaw init` as the bootstrap companion

#### Scenario: No-args invocation launches dashboard on configured install

- **GIVEN** `netclaw.json` exists
- **WHEN** the operator runs `netclaw config`
- **THEN** the domain-oriented dashboard launches

#### Scenario: Missing install refuses with plain message

- **GIVEN** `netclaw.json` does not exist
- **WHEN** the operator runs `netclaw config`
- **THEN** stderr contains `No configuration found. Run \`netclaw init\` first.`
- **AND** the command exits non-zero
- **AND** no partial TUI starts
