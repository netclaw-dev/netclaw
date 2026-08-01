## Context

Netclaw currently executes `/bin/bash -c` on Unix and `cmd.exe /c` on Windows. Security policy uses ShellSyntaxTree 0.1.5 for Bash but retains separate handwritten Windows semantics. Pipeline parsing produces multiple clauses, yet approval extraction and some policy consumers only evaluate the head. Working-context snapshots already provide a cache-stable, audience-aware seam for Git-derived runtime facts.

The change crosses execution, security, session context, prompt guidance, and native platform testing. Persisted approval entries must remain consumable with platform-correct comparison rules.

## Goals / Non-Goals

**Goals:**

- Make execution, parsing, approval, and agent guidance agree on Bash or PowerShell.
- Evaluate every executable pipeline clause before starting a process.
- Reuse the existing working-context snapshot pipeline without changing persisted actor message shapes.
- Preserve byte-prefix caching and fail loudly for missing or unsupported shells.
- Deliver four independently safe review stages: dependency foundation, Bash hardening, PowerShell/context enablement, and Windows approval completion.

**Non-Goals:**

- Supporting `cmd.exe`, fish, zsh, dash, or ash as first-class grammars.
- Inferring a user's interactive login shell.
- Adding a configurable shell fallback or silently translating commands.
- Changing tool schemas, audience grants, or approval decision persistence shapes.

## Decisions

### One immutable execution environment

Introduce a required immutable value describing OS family, executable, grammar, and path style. A single resolver constructs it during daemon composition and the same value selects the `ShellTool` process, ShellSyntaxTree parser, security semantics, and model-visible working context.

Alternative considered: let each subsystem call `OperatingSystem.IsWindows()`. Rejected because parallel detection recreates the current disagreement and makes tests platform-dependent.

### ShellSyntaxTree is the structural authority

Upgrade to 0.2.0-alpha and adapt Netclaw behind its own small parser/semantics interface. Parse once per policy evaluation and retain ordered executable clauses. Every hard-deny, safe-verb, approval, trust-zone, and display consumer examines the same clause representation. Dynamic or unsupported constructs produce a deny or approval requirement according to the existing caller's security posture; they never become automatically safe.

Alternative considered: extend the handwritten Windows tokenizer. Rejected because it duplicates upstream PowerShell parsing and cannot reliably cover aliases, nested command strings, or encoded commands.

### PowerShell 7 is the Windows shell

Windows uses `pwsh` with non-interactive command execution. Absence of `pwsh` is an explicit execution error and startup/runtime diagnostics expose the prerequisite. No `powershell.exe` or `cmd.exe` fallback is permitted.

### Execution context rides the volatile working-context tail

Add an execution-environment inspector to the existing `WorkingContextSnapshotProvider` and render its immutable facts as an `execution_environment` subsection. The full volatile context remains inserted into history before the user message. Earlier bytes therefore remain unchanged while each new turn appends a fresh nudge. The provider remains the single composition seam used by sessions and child runs.

Alternative considered: a second context provider or a `OnceAtStart` layer. Rejected because DI expects one working-context provider and current `OnceAtStart` content disappears after initial assembly.

### Guidance belongs to the embedded operating core

The full embedded `AGENTS.md` tells tool-capable agents to consult the execution environment and not mix grammars. Detailed examples live in the versioned `netclaw-operations` skill. The operator-authored deployment playbook remains untouched. The Public core changes only if discovery shows Public can receive shell execution.

### No persistence migration unless canonical compatibility tests fail

Existing approval records remain unchanged. Platform-aware parsing must emit candidates that the current comparer and approval store consume. Load/round-trip tests prove compatibility; any incompatible legacy record fails visibly rather than being silently reinterpreted.

## Risks / Trade-offs

- [ShellSyntaxTree prerelease API changes] → isolate it behind Netclaw-owned semantics and pin the exact package version.
- [Policy consumers diverge] → build candidates once and add cross-consumer pipeline-tail tests.
- [Windows hosts lack `pwsh`] → fail with an actionable message and validate on native Windows CI.
- [Repeated environment text adds tokens] → keep the subsection short; accept the small cost to reuse proven cache-stable tail behavior.
- [Prompt guidance changes model behavior] → add deterministic assembly tests and run behavioral evals.
- [PowerShell aliases obscure canonical verbs] → use the parser's canonical verb and dynamic markers; never auto-allow unresolved verbs.

## Migration Plan

1. Upgrade and adapt the parser while preserving runtime shell selection.
2. Harden Bash pipeline evaluation and ship the security fix.
3. Introduce the canonical environment, switch Windows to `pwsh`, and inject guidance/context with native Windows proof.
4. Complete Windows directory approvals and legacy round-trip validation.

Rollback is per stage. The PowerShell stage may be reverted without reverting the preceding Bash hardening. No persisted schema migration is expected.

## Open Questions

None. Supported grammars, Windows executable, context placement, fallback policy, and delivery order are fixed by this design.
