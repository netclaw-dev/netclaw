## Context

See proposal.md for the reasons. `ShellCommandAnalysis` classifies each
command occurrence. `ShellApprovalMatcher` makes the exact candidate.
`ShellPolicyCoordinator.SelectCommandWordsCorrection` chooses between the
rewrite correction and the prompt. All of this state is call-local. Nothing
persists, and no actor message changes shape.

## Goals / Non-Goals

**Goals:**
- Give the rewrite correction back to a command that is exact only because of
  the pathname-expansion rule.
- Treat `exit` and `return` as data commands.

**Non-Goals:**
- A typed parser cause for a rejected source. It needs a new ShellSyntaxTree
  fact.
- A fact that separates an unquoted glob character from an unquoted
  expansion. It also needs a new ShellSyntaxTree fact.

## Decisions

- The analysis applies the pathname-expansion rule after the other causes.
  When the rule is the only cause, the analysis records the occurrence in a
  call-local set. The matcher copies this fact to the exact candidate as
  `WordRewriteCanResolve`. The coordinator then lets
  `ClassifyUnknownCommandWords` decide the rewrite, as it does for a command
  with unknown words.
  - Alternative: skip the rule when the command words are unknown. Rejected:
    `cat ~/.netclaw/{keys,config}/key-1.xml` then becomes Allowed by the
    reviewed-safe policy.
- The candidate stays `Command`, so no grant, no reviewed phrase, and no D1
  rule covers it. The flag changes only the choice between a correction and a
  prompt.
- `exit` and `return` join `break` and `continue` in one Bash-only
  control-transfer set. The parser accepts only a bounded operand, so the
  operand cannot run code.
- The rule does not read `MayFieldSplit`. See the spec for the reason.

## Risks / Trade-offs

- [The brace credential rows now get a correction, not a prompt] → The call
  does not run. The rewritten literal paths get their own path checks, and the
  credential paths are denied. The owner accepted this outcome.
- [A beta.18 brace word has no public cause] → `git {push,fetch} origin` gets
  the general advice "Write the command words literally", not "Run each
  command separately". A typed cause from ShellSyntaxTree can restore the
  specific advice.
- [Failure mode: a missing flag] → The coordinator keeps the prompt. That is
  the fail-closed default.
