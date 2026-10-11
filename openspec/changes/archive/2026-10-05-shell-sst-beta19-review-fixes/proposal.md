## Why

The review of #2349 found four gaps in the TA-7 adoption of ShellSyntaxTree
0.4.0-beta.18 and 0.4.0-beta.19. The pathname-expansion rule removed the
rewrite correction from calls that a rewrite can fix. `exit` and `return`
still prompt, although the parser now reads them. The spec does not say why
the rule ignores `MayFieldSplit`, and one scenario names a catalog case that
does not exist. The source PRD is the tool authorization PRD
(`docs/prd/`, TA-7).

## What Changes

- When the pathname-expansion rule is the only cause that makes a command
  exact, the call gets the rewrite correction again. The command stays exact,
  so no grant and no reviewed phrase covers it. Examples:
  `git {push,fetch} origin` and `for f in '*.cs'; do cat /work/$f; done`.
- `cat ~/.netclaw/{keys,config}/key-1.xml` with a global `cat` grant gets a
  rewrite correction, not a prompt. The call does not run.
- `exit` and `return` are Bash data commands, as `break` and `continue` are.
  `cd x || exit 1; ls` needs no grant for `exit`.
- The spec states why the rule does not read `MayFieldSplit`.
- The scenario for a quoted unknown output part names the correct catalog
  case, `unknown-glob-word-output-keeps-glob-rule`.

In scope: TA-7 shell analysis only. Out of scope: a typed parser cause for a
rejected source (the JSON program-word rows) and a fact that separates an
unquoted glob character from an unquoted expansion. Both need new
ShellSyntaxTree facts.

## Capabilities

### New Capabilities

### Modified Capabilities
- `tool-authorization`: TA-7 states the rewrite correction for an
  expansion-only exact command, the control-transfer builtins, and the
  `MayFieldSplit` reason.

## Impact

- `ShellCommandAnalysis`, `ApprovalCandidate`, `ShellApprovalMatcher`,
  `ShellPolicyCoordinator`, and `ShellVerbPolicyData`.
- The review catalog and its table, `ShellConfigReadTests`, the mutation
  tests, and the mutation script (a new target for `IsProvedValueDenied`).
- Security: a correction grants no authority. The call does not run, and the
  rewritten call passes normal approval. An `exit` or `return` operand cannot
  run code, and a redirect or a substitution keeps its own check.
- Operations: no configuration change. The agent sees a correction in place of
  some prompts.
