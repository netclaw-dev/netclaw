## Why

Bare integer tokens (ticket IDs, port numbers, timeouts) in shell commands were being baked into approval verb patterns, causing every unique integer value to require a separate approval. For example:

```
freshdesk ticket get 123  →  pattern: "freshdesk ticket get 123"
freshdesk ticket get 456  →  pattern: "freshdesk ticket get 456"
```

Each unique value creates a distinct `ApprovalEntry`, forcing redundant approval prompts. The AST parser (ShellSyntaxTree) correctly strips integers from `VerbChain` via `IsVerbLikeToken` (requires `[a-z]` start), but the display/retry-key path uses raw whitespace tokenization, which includes integers.

## Source PRDs

- `PRD-002` (Gateway Security Envelope): default-deny, fail-closed approval as the single authoritative tool-access boundary.

## What Changes

- Update the verb-chain extraction spec to explicitly include bare integers as a termination condition alongside flags, paths, and URLs.
- Add a rationale for why integers are excluded: they represent call-specific values that vary between invocations of the same verb chain.
- No code or test changes are part of this spec delta — those are in the same PR under `src/Netclaw.Security/` and `src/Netclaw.Security.Tests/`.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `tool-approval-gates`: verb-chain extraction termination conditions now include bare integer tokens; the spec documents this explicitly to prevent future regressions.

## Impact

- Affected code: `src/Netclaw.Security/IToolApprovalMatcher.cs` (ReconstructClauseText + IsBareIntegerToken), `src/Netclaw.Security.Tests/ShellApprovalMatcherTests.cs` (4 new POSIX-only tests).
- Security impact: narrows approval patterns, reducing false-positive matches from overly broad commands while eliminating false-negatives from integer-specific patterns.
- Operational impact: fewer redundant approval prompts; approval store stays lean with one entry per verb chain rather than one per unique ticket/port/timeout value.
- Compatibility impact: POSIX-only change (Windows uses the legacy ShellTokenizer path, which retains the old behavior for now). Existing approval entries are unaffected — they match the verb chain, not the display pattern.

## Out of Scope

- Windows/MacOS fix (ShellSyntaxTree is bash-only).
- Adding integers as a termination condition to the AST itself (IsVerbLikeToken already handles this — the gap is only in display/retry-key extraction).
- Windows integration of PwshParser into the approval matcher (deferred to a future change).
