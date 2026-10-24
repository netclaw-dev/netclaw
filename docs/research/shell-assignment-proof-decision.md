# Bash assignment proof decision

## Decision

The parent accepts a bounded Bash implementation experiment on 23 October 2026.
The user authorized this work through the active shell approval goal.
This decision permits product edits. It does not claim that the implementation passes its gates.

The candidate may accept a parser-proved exact effective Bash assignment value when its authored value is unknown.
PowerShell retains the existing exact-authored requirement.
The candidate must preserve the digest format, assignment scope, export reach, and exact effective value.
The candidate must leave source reconciliation, host selection, wrapper analysis, path checks, and grant matching intact.
No new environment snapshot, variable allow list, source-expression parser, or authority flag is necessary.

## Evidence before product edits

The baseline is `aecbe73e1714e9acc2d68612e0cfa0d3abc4f33d`.
The author tested the exact pinned ShellSyntaxTree `0.4.0-beta.24` package and audited its compiled implementation.
The proof memo records the package assembly hash, tool version, source contracts, and test command.
The baseline digest tests pass 52 cases and fail two new positive cases at the exact-authored guard.
The preceding parser, source-span, scope, and environment-reach assertions pass in those two cases.

The public assignment facts retain the name, authored value, effective value, scope, export reach, and source span.
Command substitution makes the assignment value unknown and exposes its nested effect separately.
Branch joins keep only compatible state facts.
Direct source gets the existing launch snapshot. Wrapper child source gets no launch values.
The parent inspected both digest call sites and the existing source reconciliation gate.
The independent reviewer supports the direct Bash case and rejects an unconditional cross-shell relaxation.

Actual context probes supply these controls:

- Unknown Bash rejects the tested source-binding forms before it publishes commands.
- Fresh Bash can prove source-derived exact values, but the tested export operands still have unresolved command words.
- The tested PowerShell variable-derived assignments remain unsupported. Its bounded literal assignment keeps both exact facts.
- A `$HOME` assignment on an unknown Bash host remains unresolved.
- A fallback child shell receives no launch-derived value proof.

These are observed cases, not a proof that every shell source is safe.
The implementation must retain the existing complete-analysis and authority gates for all other sources.

## Required acceptance evidence

The launch-derived and literal forms must produce the same qualified identity under the same shell and export state.
A qualified grant must allow that exact effect through the real actor path.
A plain grant and a changed effective value must still require separate consent.
Unknown values, hidden substitutions, protected paths, links, unknown directories, and redirects must retain their existing checks.
Assignments to `PATH`, `LD_PRELOAD`, and `GIT_DIR` must keep their environment qualifiers.
PowerShell's digest behavior must stay unchanged.

Run focused security and actor tests, focused assignment mutants, and the historical corpus with all current host states.
Review each changed candidate, consent option, authorization result, and execution fact.
Run runtime skill evals if mapped guidance changes.
An unsafe transition rejects the implementation until the underlying proof defect is resolved.

## Limits

This decision does not authorize deployment, a live grant edit, or an SST release.
The loop-redirect study stays separate.
The incident corpus uses synthetic grants and incomplete initial-directory evidence.
The actor and matcher tests must prove the actual grant boundary.
