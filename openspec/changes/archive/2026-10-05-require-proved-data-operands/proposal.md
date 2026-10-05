## Why

Source PRD IDs: `PRD-002` SEC-003 and SEC-006.

The adversarial review of #2344 found that a Bash data command with no
redirect lost its assignment digest for every operand. ShellSyntaxTree
0.4.0-beta.17 gives no path for an unquoted word with a bound value, such as
`d=key; echo ../netclaw/"${d}s"/*`. That word could list a protected folder
with no prompt, while its literal twin is denied.

## What Changes

- A Bash data command skips the assignment digest only when each operand is
  proved data. An output operand is proved data with an exact value, a finite
  set, or one double-quoted raw word. A test operand needs an exact value or a
  finite set with no `[`.
- `n=$(cmd); echo "$n"` stays data. `echo $n` and an unquoted glob that is
  built from a variable keep their digest and need consent.

In scope: the assignment digest rule of TA-7.

Out of scope:

- An unquoted `echo $(...)` with no assignment (#2315). It can list the names
  in a folder. A ShellSyntaxTree fact for pathname expansion will close it.
- A saved `test` grant that covers a `-v` subscript. This is the accepted
  class of a granted command that runs code through a feature.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-7 limits the assignment digest skip to proved data
  operands.

## Impact

- Code: `ShellCommandAnalysis.HasProvedDataOperands` and
  `ShellApprovalMatcher.TryCreateAssignmentDigest`.
- Security: a denial cannot become an allow through a bound glob word.
- Operations: an unquoted word with a run-time value prompts again.
