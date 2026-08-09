## Why

PRD-002 and PRD-006 require default-deny shell approval with useful approval
reuse. Netclaw still uses legacy token splitting for PowerShell child-process
commands, so it cannot safely reuse narrow approvals for complete PowerShell
pipelines and execution regions.

## What Changes

- Update Netclaw from ShellSyntaxTree `0.3.0-alpha.1` to
  `0.3.0-alpha.2`.
- Detect one exact PowerShell host argv shape that the active Bash host can
  pass to a child process.
- Parse each complete PowerShell payload with `PwshParser` in the safe
  `Unknown` initial-state mode.
- Evaluate the outer PowerShell host occurrence and every child occurrence
  before Netclaw reuses a safe verb or stored approval.
- Add an approval review matrix for complete read-only commands, pipelines,
  executable script-block regions, data script blocks, dynamic values,
  command-resolution changes, hard-deny rules, and protected paths.
- Keep the raw command and one-shot approval as the fallback when either the
  outer shell or PowerShell analysis is incomplete.

In scope: the exact `pwsh` command wrapper under the POSIX Bash host,
PowerShell occurrence analysis, the package reference, focused security tests,
the approval review matrix, and the canonical approval specification.

Out of scope: `powershell`, `powershell.exe`, and `pwsh.exe` runtime identity,
Windows `cmd.exe` wrapper reuse, a new shell tool argument, a direct PowerShell
execution host, PowerShell profile or module baselines, isolated initial-state
claims, `Start-ThreadJob` special handling, analysis of external `.ps1` file
contents, new grant shapes, and stable ShellSyntaxTree v0.3.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `tool-approval-gates`: Add complete PowerShell child-command analysis before
  Netclaw reuses safe verbs or stored shell approvals.

## Impact

- Code: shell analysis and approval matching in `Netclaw.Security`.
- Dependency: ShellSyntaxTree changes to `0.3.0-alpha.2`.
- Tests: focused parser-consumer tests and the approval review matrix.
- Security: incomplete outer-shell syntax, incomplete PowerShell syntax,
  unknown command identity, unknown executable regions, and all Windows
  PowerShell wrappers stay strict.
- Operations: no configuration, stored approval, or migration change is
  required.
