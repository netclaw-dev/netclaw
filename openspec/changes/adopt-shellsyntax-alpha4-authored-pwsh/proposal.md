## Why

PRD-002 and PRD-006 require strict shell approval with useful grant reuse.
Netclaw still treats some complete authored PowerShell commands as complex because older parser results tied completeness to ambient command resolution.

## What Changes

- Update ShellSyntaxTree from `0.3.0-alpha.2` to `0.3.0-alpha.4`.
- Accept complete static child occurrences from the existing exact POSIX `pwsh` wrapper.
- Keep the outer `pwsh` host as an independent approval candidate.
- Treat a parser-proved, source-level `Write-Output` script block as data instead of hidden execution.
- Add allow, prompt, and strict matrix cases for static commands, pipelines, data blocks, unknown values, and source mutations.
- Keep PowerShell knowledge limited to submitted syntax and explicit parser inputs.

In scope: the existing exact POSIX wrapper, authored child occurrences, focused consumer policy, and approval tests.

Out of scope: profile inspection, module discovery, alias or function discovery, `PATH` lookup, inherited-variable inspection, prior-runspace inspection, Windows wrapper reuse, and external script contents.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-approval-gates`: Reuse approvals for complete authored PowerShell child commands without claims about ambient runtime resolution.

## Impact

- Code: PowerShell child analysis and dynamic-value checks in `Netclaw.Security`.
- Dependency: ShellSyntaxTree changes to `0.3.0-alpha.4`.
- Tests: focused security tests and the shell approval review matrix.
- Security: source-visible mutations, dynamic identities, policy-sensitive unknown values, and unsupported syntax remain strict. Authored completeness makes no runtime binding claim.
- Operations: no config or stored approval migration is required.
