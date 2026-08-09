## Why

PRD-002 and PRD-006 require default-deny shell approval with useful approval
reuse. Netclaw must adopt ShellSyntaxTree `0.3.0-alpha.1` because the first alpha
predates Bash command-resolution hardening, heredoc facts, and here-string facts.

## What Changes

- Update Netclaw from ShellSyntaxTree `0.3.0-alpha` to `0.3.0-alpha.1`.
- Correct the approval display contract for the v0.3 occurrence and redirect
  model. Raw command text remains the fallback when facts are incomplete.
- Add strict matrix cases for Bash command-resolution mutation and reserved
  execution forms.
- Add a narrow data-only stdin grammar for argument-free `cat` with a complete
  literal heredoc or exact and finite here-string data.
- Add allow and prompt cases for complete and unknown Bash stdin data.
- Keep hard-deny, protected-path, dynamic-value, and incomplete-parse behavior
  fail closed.

Transparent direct shell dispatch remains subject to complete recursive
analysis. Receiver wrappers such as `command cat` stay strict.

In scope: Bash approval analysis, the package reference, the approval matrix,
and the canonical approval specification.

Out of scope: PowerShell migration, stable ShellSyntaxTree v0.3, new grant
shapes, broad stdin interpreter grammars, and the deferred safe `sed` grammar.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `tool-approval-gates`: Align approval display and reuse rules with complete
  v0.3 command-occurrence and redirect facts.

## Impact

- Code: Bash shell analysis and its package dependency.
- Tests: the review matrix and focused security tests.
- Security: unknown, incomplete, mutating, and unsupported forms stay strict.
- Operations: no configuration or migration changes are required.
