## ADDED Requirements

### Requirement: PowerShell approval uses authored command completeness

For the existing exact POSIX PowerShell wrapper, Netclaw SHALL evaluate the outer `pwsh` host and every ShellSyntaxTree child occurrence independently.
It SHALL use `PwshInitialStateMode.Unknown` and SHALL limit its proof to submitted syntax and explicit parser inputs.

Netclaw SHALL exclude possible ambient profiles, modules, aliases, functions, `PATH`, inherited variables, prior runspace state, and executable contents from authored completeness.
Netclaw SHALL NOT inspect or infer those ambient facts, and authored completeness SHALL NOT claim runtime command binding.

A parser-proved `Write-Output` script-block argument SHALL remain source-level data when the occurrence is complete and the parser publishes no execution region for that argument.
The block SHALL NOT create an approval candidate for commands whose text appears inside the data block.

Dynamic identities, policy-sensitive unknown values, unresolved paths, unknown receivers, unsupported syntax, and source-visible command-resolution changes SHALL remain one-shot only.
Hard deny and protected-path checks SHALL run before safe verbs or stored approvals.

#### Scenario: Static authored cmdlet reuses grants

- **GIVEN** the outer `pwsh` host and a static authored cmdlet have matching grants
- **WHEN** the exact POSIX wrapper submits the static cmdlet
- **THEN** Netclaw reuses both grants without an extra complex-command prompt
- **AND** Netclaw does not inspect the ambient PowerShell environment

#### Scenario: Static authored pipeline evaluates every stage

- **GIVEN** an exact PowerShell payload contains a static pipeline
- **WHEN** Netclaw analyzes the payload
- **THEN** every authored stage remains a separate approval occurrence
- **AND** each stage requires safe-verb or stored-grant coverage

#### Scenario: Write-Output script block stays data

- **GIVEN** the payload is `Write-Output { Remove-Item victim.txt }`
- **WHEN** ShellSyntaxTree marks `Write-Output` complete and publishes no execution region
- **THEN** Netclaw evaluates `Write-Output` as the only child command
- **AND** `Remove-Item` does not become a command candidate

#### Scenario: Source mutation remains strict

- **GIVEN** submitted source changes the `Write-Output` command before a later invocation
- **WHEN** the later invocation receives a script block
- **THEN** the later occurrence remains incomplete
- **AND** stored grants do not authorize it

#### Scenario: Dynamic child value remains strict

- **GIVEN** a static authored child command contains an unknown policy-sensitive value
- **WHEN** Netclaw cannot prove the effective option, path, redirect, or command value
- **THEN** Netclaw offers only one-shot approval or deny
- **AND** no reusable candidate is created from the incomplete value
