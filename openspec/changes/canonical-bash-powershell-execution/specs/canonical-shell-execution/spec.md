## ADDED Requirements

### Requirement: Canonical platform shell environment

The runtime SHALL resolve one immutable execution environment used by shell process execution, syntax parsing, security policy, approval matching, and agent context. Unix-like hosts SHALL use Bash grammar and `/bin/bash`; Windows hosts SHALL use PowerShell grammar and PowerShell 7 (`pwsh`). The environment SHALL include the operating-system family, executable, preferred grammar, and path style.

#### Scenario: Unix environment selects Bash

- **GIVEN** Netclaw runs on a supported Unix-like host
- **WHEN** the execution environment is resolved
- **THEN** its executable is `/bin/bash`
- **AND** its preferred grammar is Bash
- **AND** its path style is POSIX

#### Scenario: Windows environment selects PowerShell

- **GIVEN** Netclaw runs on Windows
- **WHEN** the execution environment is resolved
- **THEN** its executable is `pwsh`
- **AND** its preferred grammar is PowerShell
- **AND** its path style is Windows

#### Scenario: Required shell is unavailable

- **GIVEN** the canonical shell executable cannot be started
- **WHEN** a shell command is requested
- **THEN** execution fails with an actionable unavailable-shell error
- **AND** the runtime does not fall back to another shell or grammar

### Requirement: Pipeline-wide structural policy

Shell input SHALL be structurally parsed using the canonical grammar before execution. Every executable clause in every pipeline SHALL be evaluated by hard-deny, safe-verb, trust-zone, approval matching, and approval display policy. A safe or approved pipeline head SHALL NOT exempt a different tail clause.

#### Scenario: Denied pipeline tail blocks execution

- **GIVEN** a pipeline begins with a safe command
- **AND** a later clause matches a hard-deny rule
- **WHEN** shell policy evaluates the command
- **THEN** the complete command is denied before process start

#### Scenario: Unapproved pipeline tail requires approval

- **GIVEN** a pipeline head has a persisted approval
- **AND** a later executable clause does not
- **WHEN** approval policy evaluates the command
- **THEN** the later clause appears in the approval candidate set
- **AND** the persisted head approval does not authorize it

#### Scenario: Dynamic syntax does not become safe

- **GIVEN** the canonical parser marks a command or verb as dynamic or unresolved
- **WHEN** safe-verb or autonomous trust policy evaluates it
- **THEN** it is not treated as an automatically safe command
- **AND** execution follows the caller's existing deny-or-approval path

### Requirement: First-class grammar boundary

Netclaw SHALL support Bash and PowerShell as first-class shell grammars. Commands using unsupported grammar or mixing Bash and PowerShell syntax SHALL NOT be silently translated or executed through a compatibility shell.

#### Scenario: PowerShell nested command parsing

- **GIVEN** a PowerShell command contains a nested `pwsh -Command` or encoded command
- **WHEN** policy parses the command
- **THEN** nested executable clauses are included in structural policy evaluation

#### Scenario: Unsupported shell grammar

- **GIVEN** a command requires an unsupported first-class grammar
- **WHEN** Netclaw attempts to classify or execute it
- **THEN** the operation fails visibly
- **AND** Netclaw does not substitute `cmd.exe`, Windows PowerShell, or another POSIX shell
