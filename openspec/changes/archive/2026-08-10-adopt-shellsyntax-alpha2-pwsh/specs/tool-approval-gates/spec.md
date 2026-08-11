## ADDED Requirements

### Requirement: Complete PowerShell child commands use occurrence approval

On a POSIX host, Netclaw SHALL prove the Bash wrapper before it parses a
PowerShell child payload. Approval reuse SHALL require exactly the direct
PowerShell 7 host token `pwsh`, `-NoProfile`, `-NonInteractive`, `-Command`,
and one quoted static non-stdin payload in that order. It SHALL reject every
other host spelling, host option, flag order, payload count, outer redirect,
and trailing argument. The `pwsh` comparison SHALL be ordinal and
case-sensitive. The three option-name comparisons SHALL be case-insensitive.
The child payload SHALL produce a complete `PwshParser` result in `Unknown`
initial-state mode.
Netclaw SHALL pass the exact Bash-decoded payload value to `PwshParser`. It
SHALL NOT ask the PowerShell parser to reinterpret the outer Bash source.

Netclaw SHALL retain and evaluate the outer PowerShell host occurrence and
every child `CommandOccurrence`, effective path, redirect, and execution
region. It SHALL apply hard-deny and protected-path rules before safe verbs and
stored approvals. An incomplete outer wrapper, PowerShell parse, command
identity, occurrence, value, redirect, or execution region SHALL produce no
persistent candidate. Netclaw SHALL NOT reuse a
PowerShell child approval through the Windows `cmd.exe` host until it has a
complete outer-host grammar or a direct PowerShell execution path.

#### Scenario: Approved host composes with safe child commands

- **GIVEN** a direct no-profile non-interactive PowerShell wrapper in a trusted project directory
- **AND** a stored approval covers the outer `pwsh` host
- **AND** its exact payload contains only complete native safe commands
- **WHEN** Netclaw evaluates the shell invocation
- **THEN** Netclaw composes the host approval with the existing safe-verb policy
- **AND** Netclaw evaluates every child occurrence

#### Scenario: Stored approval covers only the complete child command

- **GIVEN** a direct no-profile non-interactive PowerShell wrapper has an exact payload
- **AND** stored approvals match both the outer host and one complete child command
- **WHEN** Netclaw evaluates the shell invocation
- **THEN** Netclaw reuses the stored `(verb, directory)` approval
- **AND** a stored approval for `pwsh` alone does not cover the child command

#### Scenario: Inherited Bash function cannot hide behind a safe child

- **GIVEN** `BASH_ENV` or an exported function can replace the authored `pwsh` host
- **AND** the PowerShell payload contains only a safe child command
- **WHEN** no stored approval covers the outer `pwsh` occurrence
- **THEN** Netclaw requires approval for the outer host
- **AND** the safe child does not authorize the invocation by itself

#### Scenario: Proved direct script-block body receives an independent decision

- **GIVEN** a complete PowerShell payload uses the intrinsic direct-call form for a script block
- **WHEN** the script block contains a command that needs approval or hard deny
- **THEN** Netclaw evaluates that body command as a separate occurrence
- **AND** approval for the outer `pwsh` host does not cover the body command

#### Scenario: Unproved named script-block receiver stays strict

- **GIVEN** a PowerShell payload passes a script block to an unknown or unproved named receiver
- **WHEN** ShellSyntaxTree marks its execution region incomplete
- **THEN** Netclaw requires one-shot approval or deny
- **AND** Netclaw offers no persistent approval candidate

#### Scenario: PowerShell command-resolution change stays strict

- **GIVEN** a PowerShell payload changes an alias or function before a later command
- **WHEN** the later command identity is incomplete
- **THEN** Netclaw requires one-shot approval or deny
- **AND** an existing approval for the visible command name does not authorize it

#### Scenario: Nested hard deny wins before approval

- **GIVEN** a complete PowerShell child payload contains a hard-deny command
- **AND** stored approvals cover the visible command names
- **WHEN** Netclaw evaluates the shell invocation
- **THEN** Netclaw denies the complete invocation
- **AND** it does not check or reuse stored approval

#### Scenario: Decoded protected path wins before approval

- **GIVEN** outer Bash quoting or literal fragments hide a protected path from a raw-token scan
- **AND** the complete PowerShell child analysis resolves that protected path
- **WHEN** Netclaw evaluates the shell invocation
- **THEN** Netclaw denies the complete invocation before approval reuse
- **AND** an existing approval for the child command does not bypass the path rule

#### Scenario: Dynamic Bash payload stays strict

- **GIVEN** Bash can change the PowerShell command payload before launch
- **WHEN** Netclaw cannot prove the exact child source
- **THEN** Netclaw requires one-shot approval or deny
- **AND** Netclaw offers no persistent approval candidate

#### Scenario: Bash quote boundaries cannot hide a child command

- **GIVEN** adjacent Bash quote segments decode into more than one PowerShell statement
- **WHEN** the decoded payload contains a nested hard-deny command
- **THEN** Netclaw evaluates that nested command
- **AND** a stored approval for the visible outer host cannot authorize the invocation

#### Scenario: Host working-directory option stays strict

- **GIVEN** an otherwise complete wrapper adds `-WorkingDirectory` before its payload
- **WHEN** Netclaw evaluates the PowerShell child command
- **THEN** Netclaw does not reuse a directory-scoped child approval
- **AND** Netclaw offers no persistent approval candidate

#### Scenario: Windows PowerShell host spelling stays strict

- **GIVEN** a Bash command uses `powershell` or `powershell.exe`
- **WHEN** Netclaw cannot prove the PowerShell 7 runtime identity
- **THEN** Netclaw requires one-shot approval or deny
- **AND** Netclaw offers no persistent child approval candidate

#### Scenario: Differently cased POSIX host stays strict

- **GIVEN** a Bash command uses `PWSH` instead of `pwsh`
- **WHEN** Netclaw evaluates the executable identity
- **THEN** Netclaw requires one-shot approval or deny
- **AND** Netclaw offers no persistent child approval candidate

#### Scenario: Windows PowerShell wrapper stays strict

- **GIVEN** `cmd.exe` launches a PowerShell child command
- **WHEN** Netclaw evaluates the wrapper without a complete `cmd.exe` grammar
- **THEN** Netclaw requires one-shot approval or deny
- **AND** Netclaw offers no persistent approval candidate
