## ADDED Requirements

### Requirement: Shell execution uses the canonical native host

The `shell_execute` tool SHALL start the executable from the canonical shell
environment. It SHALL pass the submitted command as one process argument after
the environment's fixed non-interactive arguments. It SHALL close stdin and
preserve the existing timeout, output, working-directory, and process-tree
termination behavior. Buffered and streaming execution SHALL use one shared
process-start builder. The tool schema SHALL remain unchanged.

#### Scenario: Bash command process arguments

- **GIVEN** the canonical environment uses `/bin/bash`
- **WHEN** `shell_execute` starts `git status`
- **THEN** the process arguments are `-c` and `git status`
- **AND** the tool does not invoke PowerShell or `cmd.exe`

#### Scenario: PowerShell command process arguments

- **GIVEN** the canonical environment uses a PowerShell executable
- **WHEN** `shell_execute` starts `Get-ChildItem`
- **THEN** the fixed arguments include `-NoLogo`, `-NoProfile`, and
  `-NonInteractive`
- **AND** `-Command` precedes one `Get-ChildItem` argument
- **AND** the tool does not invoke `cmd.exe`

#### Scenario: Missing selected executable fails visibly

- **GIVEN** the environment selected a PowerShell executable
- **AND** the process cannot start that executable
- **WHEN** `shell_execute` runs
- **THEN** the result identifies the required executable
- **AND** the tool does not run the command through another shell

#### Scenario: Buffered and streaming execution use the same host

- **GIVEN** one canonical environment and one submitted command
- **WHEN** buffered and streaming execution build their process start data
- **THEN** both use the same absolute executable path
- **AND** both use the same fixed arguments in the same order
- **AND** both append the submitted command as one argument
