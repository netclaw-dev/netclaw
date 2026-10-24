## Why

The `0.28.0-beta.2` incident has repeated approval requests for assignments whose effective values the parser proves.
Legacy grant diagnostics also hide some phrase and assignment mismatches behind `NoGrant`.

Source PRDs: `PRD-002` SEC-003, SEC-006, SEC-007, and SEC-009; `PRD-006` tool access controls.
The [engineering glossary](../../../../docs/spec/GLOSSARY.md) owns shared terms.

## What Changes

- Explain legacy grant near misses without changing which calls a grant covers.
- Preserve the existing single-token program exceptions and all grant scopes.
- Test a generic proof for assignments that use exact launch values, such as `$HOME`.
- Permit a product change only after independent review proves source, value, and effect accounting.
- Keep the assignment name, scope, export state, and effective value in its existing digest.
- Reuse the corpus runner and add sanitized incident representatives with explicit negative controls.
- Record compound-loop redirect feasibility separately. Do not implement that parser capability in this change.

The diagnostic repair is in scope now.
The assignment product change depends on the proof gate in the design and tasks.
An evidence-backed rejection of that product change is a valid result.
No new tool, configuration option, grant kind, or executable-specific parser is in scope.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: explain legacy phrase near misses and define the proof gate for launch-value assignment identity.

## Impact

The affected owners are shell analysis, assignment digest creation, grant diagnostics, and their existing tests.
The study starts from `aecbe73e1714e9acc2d68612e0cfa0d3abc4f33d` with ShellSyntaxTree `0.4.0-beta.24`.
The implementation and validation summary is in [PR 2665](https://github.com/netclaw-dev/netclaw/pull/2665).

### Security and operational impact

The diagnostic slice must preserve all allow, deny, correction, and consent outcomes.
The assignment slice may expose reusable consent only for a source-proved exact assignment under the existing launch contract.
Existing unqualified grants must not cover a qualified assignment.
Hard deny, protected paths, symbolic links, audience checks, and launch checks retain their authority.
The change does not alter the live daemon or saved approvals.
Runtime skill changes require the existing behavioral eval gate.
Raw session data stays outside Git.
