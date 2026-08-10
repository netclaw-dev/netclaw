## Why

Source PRDs: `PRD-001`, `PRD-002`, `PRD-006`.

Netclaw executes `cmd.exe` on Windows, while its parser and approval policy do
not share one PowerShell host identity. The new ShellSyntaxTree alpha.5 dialect
contract now lets Netclaw select, describe, parse, authorize, and execute one
native Windows PowerShell environment without cross-language analysis.

## What Changes

- Update ShellSyntaxTree to `0.3.0-alpha.5`.
- Resolve one immutable shell environment during daemon composition.
- Keep `/bin/bash` and Bash grammar on Linux and macOS.
- On Windows, prefer a compatible `pwsh.exe` host from the supported PowerShell
  7.6 range. Fall back to Windows PowerShell 5.1 through `powershell.exe`.
- Store and execute the absolute path of the executable that passed the version
  probe. Do not repeat executable lookup after authorization.
- Carry the selected platform, executable, grammar, and `PwshDialect` through
  process execution, parsing, hard deny, protected paths, approval matching,
  safe-verb policy, and the model-visible working-context tail.
- Remove Netclaw's POSIX-only PowerShell child-payload parser. Bash treats a
  `pwsh` invocation as one Bash external command. Native PowerShell input uses
  `PwshParser` and retains only same-language recursion.
- Keep parser initial state unknown. Netclaw does not infer profiles, modules,
  inherited variables, command lookup, or external script contents.
- Keep the shell tool schema and stored approval record shape unchanged.
- Retire the superseded `adopt-shellsyntax-alpha2-pwsh` change. Do not sync its
  obsolete cross-language requirement.

In scope: native Windows host selection, exact dialect selection, one shared
runtime identity, the context hint, the package update, and native Windows
approval evidence.

Out of scope: a configurable user shell, `cmd.exe` grammar, cross-language
payload analysis, profile or module inspection, external `.ps1` analysis,
`Start-ThreadJob` policy, and a runtime retry through another shell.

## Capabilities

### New Capabilities

- `canonical-shell-execution`: Define platform shell selection, PowerShell
  dialect selection, immutable runtime identity, and explicit failure behavior.

### Modified Capabilities

- `netclaw-tools`: Make `shell_execute` start the selected native shell with
  grammar-correct non-interactive arguments.
- `tool-approval-gates`: Parse and authorize commands with the selected host
  grammar and dialect. Remove cross-language PowerShell child analysis.
- `netclaw-session`: Add the exact platform and shell identity to the
  model-visible working-context tail for shell-capable sessions.

## Impact

- Code: daemon composition, shell process execution, shell analysis, security
  policy, approval matching, working-context snapshots, and system guidance.
- Dependency: ShellSyntaxTree changes from `0.3.0-alpha.2` to
  `0.3.0-alpha.5`.
- Tests: add host-selection, parser-boundary, execution, context, hard-deny,
  protected-path, safe-verb, approval, and Windows matrix cases.
- Security: unknown dialects, incomplete syntax, dynamic facts, and unavailable
  compatible hosts fail closed. Stored approvals never bypass hard deny.
- Operations: Windows hosts need either compatible PowerShell 7.6 or Windows
  PowerShell 5.1. Startup reports a clear error when neither contract is met.
- Persistence: no actor message, tool argument, approval entry, or config
  migration is required.
