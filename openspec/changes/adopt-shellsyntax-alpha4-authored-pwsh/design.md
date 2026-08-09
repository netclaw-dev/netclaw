## Context

Netclaw proves one exact POSIX `pwsh` wrapper and parses its payload with `PwshInitialStateMode.Unknown`.
The analyzer keeps the outer host and each PowerShell child as separate approval occurrences.

ShellSyntaxTree alpha.4 defines occurrence completeness as a proof about submitted syntax.
It does not claim which runtime command an ambient environment selects.

Netclaw currently rejects every compatibility `DynamicSkip` argument before it evaluates the parser's occurrence proof.
This rule incorrectly treats a parser-proved `Write-Output { ... }` data block as hidden execution.

The approval gate runs before actor dispatch.
This change does not alter actor messages, persistence, recovery, tool arguments, or stored grant records.

## Goals / Non-Goals

**Goals:**

- Reuse grants for complete static PowerShell child occurrences.
- Keep the outer `pwsh` host as a separate approval candidate.
- Accept only parser-proved, source-level `Write-Output` script-block data.
- Keep every policy-sensitive unknown or incomplete fact strict.
- Avoid all inspection of the ambient PowerShell environment.

**Non-Goals:**

- Inspect profiles, modules, aliases, functions, `PATH`, variables, prior runspaces, or executable contents.
- Support new PowerShell wrappers or Windows `cmd.exe` grammar.
- Read external `.ps1` files.
- Add a PowerShell command catalog to Netclaw.
- Change grant persistence or actor behavior.

## Decisions

### Trust the parser's authored occurrence proof

Netclaw will consume alpha.4 `CommandOccurrence.IsComplete` for the existing exact child payload.
This is an authored-syntax proof. It does not prove the runtime binding of a command name.
Ambient command resolution is outside the submitted command text and outside this analysis.

Netclaw will keep `PwshInitialStateMode.Unknown`.
This mode prevents exact loop-value claims when the source does not prove the value.

Alternative: assert an isolated initial state from `-NoProfile` and `-NonInteractive`.
That choice would claim facts about inherited state that the executor cannot prove.

### Keep the dynamic-value gate except for proved Write-Output data blocks

Netclaw will retain its existing checks for dynamic identities, unknown paths, unresolved variables, redirects, and unsupported syntax.

One narrow exception will accept a `DynamicSkip` argument only when all facts below hold:

- the parser marks the occurrence complete;
- the occurrence belongs to the decoded PowerShell child;
- the parser identifies the authored receiver as `Write-Output` under the supported source-level semantics;
- the argument is a complete authored script-block token;
- the parser publishes no execution region for that argument.

This exception uses parser-owned authored receiver and region facts.
It does not infer runtime aliases, inspect the runspace, or claim runtime command binding.

Alternative: ignore all `DynamicSkip` arguments on complete occurrences.
That choice could hide an unknown path, option, value, or future syntax shape.

Alternative: keep every script block strict.
That choice preserves approval spam for a case whose non-execution semantics the parser now proves.

### Keep all security gates in their current order

Hard deny and protected-path checks will still run before safe verbs and stored approvals.
The outer `pwsh` host and all child occurrences will still require independent coverage.

Source-visible alias, function, module, or computed-execution changes will keep later occurrences incomplete.
The gate will offer one-shot approval when any required fact stays unknown.

## Risks / Trade-offs

- [Risk] A future parser changes script-block data representation. -> Exact shape checks fail closed and matrix tests detect the change.
- [Risk] Ambient runtime shadowing changes `Write-Output`. -> The model covers authored text only and still requires separate host approval.
- [Risk] A broad dynamic-data exception hides a sensitive value. -> The exception is receiver-specific, argument-specific, and parser-proof-specific.
- [Risk] NuGet package behavior changes later. -> The package version and approval matrix pin the accepted contract.

## Migration Plan

1. Update the central package version.
2. Add focused analyzer and matcher tests.
3. Add allow, prompt, and strict review-matrix rows.
4. Run security, actor, OpenSpec, header, Slopwatch, and full solution checks.

Rollback restores the prior package and dynamic-argument rule.
Stored approvals and actor state need no migration.

## Open Questions

None.
