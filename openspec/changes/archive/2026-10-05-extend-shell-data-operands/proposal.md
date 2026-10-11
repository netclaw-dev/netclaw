## Why

Source PRD IDs: `PRD-002` SEC-003, SEC-006, and SEC-008.

Live replays with the owner's grants show prompts for commands with no
authority. `[ 3 -gt 2 ] && echo yes` prompts for `[`. A loop guard
`[ "$d" = a ]` gets bad rewrite advice. In `n=$(basename x); echo "$n"`, the
`echo` becomes an exact candidate. The owner asked for fewer prompts with the
fewest moving parts.

## What Changes

- The Bash test builtins (`test`, `[`) become data commands. A data command
  needs no approval, and its path operand is not a scope.
- An operand of a test builtin is data only when the parser proves a bounded
  value with no `[`. Bash evaluates an array subscript in a `-v` operand as
  arithmetic, and the arithmetic runs a command substitution:
  `[ -v 'a[$(cmd)]' ]` runs `cmd`. Other operands stay one exact candidate.
- A Bash data command with no redirect gets no assignment digest. A run-time
  value (`$(...)`, `read`) in an `echo` or `printf` operand is data.
- `continue` and `break` inside a loop stay unresolved. ShellSyntaxTree
  0.4.0-beta.17 rejects them, so the fix belongs to a later parser release.

In scope: the Bash data-position rule and its coverage.

Out of scope:

- `continue` and `break` (ShellSyntaxTree).
- `[[ ... ]]`, which ShellSyntaxTree does not split into commands.
- PowerShell. In PowerShell, `test` is not a builtin.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-7 extends the data-position rule to the test
  builtins and to run-time values. TA-8 lists the test builtins as
  approval-exempt data commands.

## Impact

- Code: `ShellCommandAnalysis`, `ShellApprovalMatcher`,
  `ShellVerbPolicyData`, and `ApprovalPatternMatching` in `Netclaw.Security`.
- Security: a literal protected path in a test operand stays denied by the
  protected-path screen. A test builtin reveals only whether a path exists. A
  value that the parser cannot prove keeps its prompt, so a computed path
  cannot probe the credential store without consent.
- Operations: fewer prompts. The `netclaw-operations` skill and the approval
  runbook describe the rule. No configuration or persistence changes.
