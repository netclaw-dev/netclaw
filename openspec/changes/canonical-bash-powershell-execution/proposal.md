## Why

Shell approval and hard-deny policy currently reason about Bash pipelines incompletely, allowing a safe or approved pipeline head to hide a different tail command. Windows execution also uses `cmd.exe` while the parser and agent guidance assume Bash, so execution, security policy, and model-generated syntax can disagree.

Source PRDs: PRD-001, PRD-002, PRD-006, and PRD-007. Tracking issues: #1693, #964, #965, and #899.

## What Changes

- Upgrade ShellSyntaxTree to its PowerShell-capable `0.2.0-alpha` API and introduce one canonical Bash/PowerShell parser selection.
- Evaluate every shell pipeline clause consistently for hard denies, safe verbs, trust-zone checks, approval matching, and approval display.
- **BREAKING**: execute Windows shell commands with PowerShell 7 (`pwsh`) instead of `cmd.exe`; fail loudly when the required shell is unavailable.
- Surface the runtime platform, shell executable, preferred grammar, and path style through the existing cache-stable working-context snapshot pipeline.
- Teach the embedded operating core and the `netclaw-operations` system skill to follow the declared execution environment rather than assuming Bash.
- Complete Windows directory-scoped approval behavior using the same canonical command/path representation consumed at runtime.
- Add native Windows/PowerShell and prompt-prefix regression coverage.

In scope for MVP: Bash on Unix-like hosts, PowerShell 7 on Windows, pipeline-wide policy evaluation, runtime context, approval persistence compatibility, and automated validation. Out of scope: first-class `cmd.exe`, fish, zsh, dash, or ash grammars and silent compatibility fallbacks.

## Capabilities

### New Capabilities

- `canonical-shell-execution`: Canonical platform-to-shell selection, grammar parsing, fail-closed behavior, and pipeline-wide security evaluation.

### Modified Capabilities

- `netclaw-tools`: Shell execution and approval policy use the canonical grammar and all command clauses.
- `netclaw-session`: Working-context snapshots carry the execution environment without invalidating prior prompt-prefix bytes.
- `netclaw-agent-memory`: The embedded operating core grounds shell generation in the runtime execution environment.

## Impact

Affected areas include `ShellTool`, shell security and approval matchers, daemon DI, working-context snapshot assembly, embedded system-prompt resources, the operations skill, ShellSyntaxTree package APIs, Windows approval persistence, and Linux/Windows CI.

Security impact is positive but high-risk: the change closes pipeline-tail policy bypasses and removes grammar ambiguity. Unsupported/dynamic syntax and missing required shells fail closed. Operationally, Windows installations must have PowerShell 7 available; diagnostics and tests must make that prerequisite explicit.
