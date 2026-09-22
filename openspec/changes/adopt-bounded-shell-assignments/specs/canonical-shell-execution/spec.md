## ADDED Requirements

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
