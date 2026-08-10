## ADDED Requirements

### Requirement: Shell policy uses the canonical grammar and dialect

The shell policy SHALL analyze every submitted command with the canonical shell
grammar before process start. Shell hard deny, protected paths, safe verbs,
stored approvals, prompt candidates, and display SHALL use that analysis.
PowerShell analysis SHALL pass the selected
`PwshDialect` and `PwshInitialStateMode.Unknown`. Every policy consumer SHALL
evaluate the same complete command-occurrence set.

An unparseable result, an incomplete occurrence, a dynamic verb, or an unknown
policy-sensitive fact SHALL NOT produce a persistent candidate or safe-verb
auto-pass. A legacy token scan MAY block a known hard deny, but it SHALL NOT
authorize unresolved text.

#### Scenario: PowerShell pipeline evaluates every stage

- **GIVEN** the native Windows host uses PowerShell
- **AND** a pipeline contains a safe stage and an unapproved stage
- **WHEN** approval policy evaluates the command
- **THEN** it evaluates both command occurrences
- **AND** the safe stage does not authorize the unapproved stage

#### Scenario: Windows PowerShell 5.1 rejects pipeline chains

- **GIVEN** the selected dialect is `WindowsPowerShell51`
- **AND** the command uses `&&` or `||`
- **WHEN** approval policy analyzes the command
- **THEN** the result cannot create persistent approval candidates
- **AND** safe-verb policy does not allow it automatically

#### Scenario: Bash PowerShell wrapper is not cross-parsed

- **GIVEN** the canonical grammar is Bash
- **AND** the command is `pwsh -NoProfile -Command 'Get-Content ./a.txt'`
- **WHEN** approval policy analyzes the command
- **THEN** it does not add a `Get-Content` child candidate
- **AND** it evaluates the authored Bash external-command occurrence

#### Scenario: Dynamic PowerShell command remains strict

- **GIVEN** the canonical grammar is PowerShell
- **AND** command identity or an executable region depends on an unknown value
- **WHEN** approval policy evaluates the command
- **THEN** no stored grant or safe verb covers the unknown occurrence
- **AND** the caller follows the existing deny-or-approval path

#### Scenario: PowerShell native hard deny precedes approval

- **GIVEN** a PowerShell command stops a process, removes a root recursively,
  or invokes `Start-Process -Verb RunAs`
- **WHEN** shell policy evaluates the command
- **THEN** a matching hard-deny rule blocks the complete invocation
- **AND** no stored approval or safe verb can bypass the denial

#### Scenario: Dialect change reparses before grant matching

- **GIVEN** a daemon restart changes the selected PowerShell dialect
- **AND** an existing stored approval remains present
- **WHEN** a new command requests authorization
- **THEN** Netclaw derives candidates with the new dialect first
- **AND** only candidates that match the stored intent can reuse that approval
