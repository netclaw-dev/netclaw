# canonical-shell-execution Specification

## Purpose

Define the native shell identity that Netclaw uses for execution, analysis,
authorization, and model context on each supported platform.

## Requirements

### Requirement: One canonical shell environment

The daemon SHALL resolve one immutable shell environment before it registers
shell execution or security services. The environment SHALL contain the
platform, absolute executable path, grammar, path style, process arguments, and
PowerShell dialect when the grammar is PowerShell. Process execution, parsing,
hard deny, protected paths, safe verbs, approval matching, and model context
SHALL use the same environment instance.

#### Scenario: Unix host selects Bash

- **GIVEN** Netclaw starts on Linux or macOS
- **WHEN** the daemon resolves its shell environment
- **THEN** the executable is `/bin/bash`
- **AND** the grammar is Bash
- **AND** the path style is POSIX
- **AND** the command argument is `-c`

#### Scenario: Consumers share one identity

- **GIVEN** the daemon resolved one shell environment
- **WHEN** it registers shell execution, policy, approval, and context services
- **THEN** every service uses that environment
- **AND** no service makes an independent operating-system shell choice

### Requirement: Windows selects a supported PowerShell dialect

On Windows, the daemon SHALL probe `pwsh.exe` before `powershell.exe`. It SHALL
select `PwshDialect.PowerShell7` only for PowerShell versions `>=7.6.4` and
`<7.7`. It SHALL select `PwshDialect.WindowsPowerShell51` only when the fallback
host reports PowerShell 5.1. The probe SHALL not load a user profile. The
environment SHALL store the absolute path of the executable that passed the
probe.

#### Scenario: Compatible PowerShell 7 wins

- **GIVEN** `pwsh.exe` reports version 7.6.4
- **AND** `powershell.exe` is also available
- **WHEN** the Windows shell environment resolves
- **THEN** the selected executable is `pwsh.exe`
- **AND** the selected dialect is `PowerShell7`
- **AND** execution uses the absolute path that passed the probe
- **AND** the fallback host does not replace it

#### Scenario: Incompatible PowerShell 7 uses the fallback

- **GIVEN** `pwsh.exe` is absent or reports a version outside the supported range
- **AND** `powershell.exe` reports version 5.1
- **WHEN** the Windows shell environment resolves
- **THEN** the selected executable is `powershell.exe`
- **AND** the selected dialect is `WindowsPowerShell51`
- **AND** execution uses the absolute path that passed the probe
- **AND** the daemon reports why it did not select `pwsh.exe`

#### Scenario: No compatible Windows host fails startup

- **GIVEN** neither Windows executable satisfies its version contract
- **WHEN** the daemon resolves the shell environment
- **THEN** startup fails with an actionable error
- **AND** the daemon does not use `cmd.exe`
- **AND** the daemon does not select an unknown parser dialect

### Requirement: A shell host never changes after authorization

The selected shell environment SHALL remain fixed for the daemon process.
Netclaw SHALL NOT retry an authorized command through another shell executable
or dialect. A daemon restart SHALL resolve a new environment and SHALL parse and
authorize each new command with that new environment.

#### Scenario: Selected executable disappears

- **GIVEN** the daemon selected `pwsh.exe`
- **AND** that executable cannot start for a later tool call
- **WHEN** `shell_execute` starts the command
- **THEN** the call fails with the selected executable in the error
- **AND** the call does not retry through `powershell.exe` or `cmd.exe`

#### Scenario: Executable lookup changes after startup

- **GIVEN** the daemon selected an absolute `pwsh.exe` path
- **AND** a later environment change puts another `pwsh.exe` first on `PATH`
- **WHEN** `shell_execute` starts an authorized command
- **THEN** it starts the path that passed the version probe
- **AND** it does not repeat executable lookup

#### Scenario: Restart changes the fallback selection

- **GIVEN** one daemon process selected Windows PowerShell 5.1
- **AND** a later daemon restart finds compatible PowerShell 7.6
- **WHEN** a new command is submitted
- **THEN** the new process parses it with `PowerShell7`
- **AND** approval policy evaluates the new parser result before execution

### Requirement: Native host grammar defines the language boundary

Netclaw SHALL parse the submitted command only with the selected native host
grammar. Bash SHALL treat `pwsh` and `powershell.exe` as external commands.
PowerShell SHALL treat `bash` as an external command. Same-language child hosts
SHALL use the nested command facts that ShellSyntaxTree returns.

#### Scenario: Bash invokes PowerShell as an external command

- **GIVEN** the selected host grammar is Bash
- **AND** the command invokes `pwsh -Command` with a static payload
- **WHEN** Netclaw analyzes the command
- **THEN** it does not parse the payload with `PwshParser`
- **AND** policy sees the Bash-authored external-command occurrence

#### Scenario: Native PowerShell keeps same-language recursion

- **GIVEN** the selected host grammar is PowerShell
- **AND** a complete command invokes a static PowerShell child host
- **WHEN** Netclaw analyzes the command
- **THEN** policy receives each PowerShell occurrence that ShellSyntaxTree returns
- **AND** it uses the child host dialect that the parser selected

### Requirement: Parser initial state matches the child process contract

Netclaw SHALL select a parser initial-state mode from the immutable native shell environment.
The selected mode SHALL describe the same new process, fixed arguments, and sanitized environment that executes the complete source.

For Bash, Netclaw SHALL select the fresh noninteractive no-startup mode only when the launch removes all parser-required startup overrides and imported functions.
It SHALL also remove inherited `LD_*`, `DYLD_*`, `LIBPATH`, and `SHLIB_PATH` loader entries.
It SHALL prove that the selected Bash runtime is GNU Bash 5.2 or 5.3.
Another Bash version SHALL use unknown initial state until ShellSyntaxTree supports it.
It SHALL keep ordinary inherited environment entries as exported scalar strings.
A Bash probe failure SHALL stop daemon startup.
A successful probe with an unsupported version SHALL use unknown initial state.
A launch contract mismatch SHALL use unknown initial state.

For PowerShell, Netclaw SHALL select isolated noninteractive no-profile mode only for a new process with profiles disabled.
It SHALL stop daemon startup when it cannot resolve a compatible PowerShell host.
A launch contract mismatch SHALL use unknown initial state.

The shell environment owns this process-local decision.
All parsing, approval, hard-deny, path, and final launch checks SHALL use that same decision.

#### Scenario: Sanitized Bash process enables bounded assignment facts

- **GIVEN** Netclaw will start a new `/bin/bash -c` process
- **AND** the child environment removes every startup override and imported function required by ShellSyntaxTree
- **WHEN** Netclaw parses the complete submitted source
- **THEN** it uses the fresh noninteractive no-startup mode
- **AND** execution uses the same sanitized child environment

#### Scenario: A fallback shell wrapper cannot reuse assignment facts

- **GIVEN** Netclaw expands a shell wrapper that ShellSyntaxTree leaves opaque
- **AND** the wrapper payload contains an assignment
- **WHEN** Netclaw analyzes the payload for approval
- **THEN** the complete call remains one-time
- **AND** hard-deny review still sees each expanded inner command

#### Scenario: A Bash probe failure stops startup

- **GIVEN** Netclaw cannot get a valid version from the Bash probe
- **WHEN** the daemon resolves its native shell environment
- **THEN** startup fails with an error

#### Scenario: A missing Bash launch condition fails closed

- **GIVEN** one required startup override can reach the child Bash process
- **WHEN** Netclaw parses the submitted source
- **THEN** it uses unknown initial state
- **AND** bounded assignments that require fresh state remain unresolved

#### Scenario: Native PowerShell uses its no-profile process contract

- **GIVEN** Netclaw will start a new native PowerShell process
- **AND** the fixed arguments disable profiles and interactive input
- **WHEN** Netclaw parses the complete submitted source
- **THEN** it uses isolated noninteractive no-profile mode
- **AND** execution uses the same fixed arguments

#### Scenario: A finite PowerShell loop requires complete public path facts

- **GIVEN** ShellSyntaxTree supplies complete public value and path facts for each loop projection
- **WHEN** isolated PowerShell analysis expands the finite loop
- **THEN** Netclaw checks every projected scope before reusable approval
- **AND** incomplete cmdlet operand path facts keep the complete call on the one-time approval path

#### Scenario: Final launch keeps the parser contract

- **GIVEN** approval analysis accepted bounded assignment facts
- **WHEN** Netclaw performs its final policy check before process start
- **THEN** it parses with the same initial-state mode
- **AND** a changed configured executable path, fixed argument set, version, or dialect prevents the strong parser mode or launch
- **AND** a startup or loader override prevents the strong Bash parser mode or launch
