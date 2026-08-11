## Why

Netclaw v0.26.0-beta.3 produced 18 distinct shell approval prompts after its
2026-08-11 15:13:55 UTC daemon start. The current policy path spreads parsing,
hard deny, path checks, candidate extraction, asynchronous grant matching,
safe-space rules, and prompt construction across several owners. A 1,655-line
matcher and repeated special cases make it difficult to prove why a call was
allowed or prompted.

The defect is architectural, not a request to parse more executables. Netclaw
needs a typed per-candidate coverage pipeline that composes stored grants with
reviewed safe policy while retaining the approval actor as the atomic owner of
session and persistent grant snapshots.

## What Changes

- Add a coordinator with a synchronous preflight, one typed approval-actor
  request, and a deterministic completion pass.
- Track authorization coverage per ShellSyntaxTree command occurrence.
- Keep hard deny and protected paths terminal and based on real execution
  facts.
- Add a separate, bounded causal approval-intent projection for Bash diagnostic
  tails without rewriting the command or model history.
- Replace flat safe-verb strings with an immutable reviewed policy catalog.
- Replace exact flat grant strings with versioned shell-token phrases and a
  decision-gated v2 migration.
- Emit a bounded, redacted decision trace that also supplies near-miss data.
- Adopt the paired ShellSyntaxTree 0.3.1 authored/effective fact separation.
- Pin the exact sanitized D01-D18 catalog and adversarial cases.

No production branch will parse an executable's private options or operands.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `tool-approval-gates`: structured shell evaluation, actor-owned grant
  coverage, causal approval intent, token phrases, safe catalog, and traces.

## Impact

- Security: hard deny and protected paths remain first and terminal; internal
  evaluator failures deny rather than prompt.
- Persistence: approval store advances to version 3 with typed token phrases;
  simple v2 authority follows the recorded maintainer choice, and ambiguous
  entries remain exact-only.
- Actors: `ToolApprovalActor` receives one batch match request and returns
  per-candidate coverage from one atomic snapshot.
- UX: covered diagnostic chains stop prompting; unresolved syntax remains
  one-time-only.
- Dependencies: implementation waits for ShellSyntaxTree 0.3.1.
- Documentation/evals: operator guidance and approval behavioral evals change.
