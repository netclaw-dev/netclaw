## Why

Two live 0.27.0 sessions showed two avoidable prompts (owner findings F2 and
F4, approved for 0.27.1). The source PRD is the tool authorization PRD
(`docs/prd/`, TA-7 and TA-8).

- F2: after a `cd` that can fail, the directory of a later command is not
  known. In an unresolved call, each later command then becomes one exact
  candidate. The approval exemption tests the candidate verb, and the verb of
  an exact candidate is its source text (`echo "---"`). Thus `echo "---"`
  prompts.
- F4: `git rev-list --count HEAD...origin/$(git branch --show-current)` has
  known command words, but the unquoted word can glob, so the command is
  exact. A grant for `git rev-list` cannot cover it, and the agent gets a
  prompt that it cannot fix.

## What Changes

- A Bash data command with no redirect and proved data operands keeps its
  normal candidate when its directory is not known. It has no path scope, so
  it keeps its approval exemption. A redirect or an unquoted unknown operand
  (`echo $n`) keeps the exact prompt.
- When the command words are known, and the pathname-expansion rule is the
  only cause that makes the command exact, the call gets a quote correction
  that names the word, attended or unattended. The call does not run. The
  quoted retry has one unknown operand, so decision D1 applies: a grant for
  anywhere covers it.
- Unknown command words, an unknown program word, an unknown redirect target,
  and every other cause keep today's handling.

In scope: TA-7 shell analysis and the TA-8 approval exemption. Out of scope:
any program grammar, and a new ShellSyntaxTree fact.

## Capabilities

### New Capabilities

### Modified Capabilities
- `tool-authorization`: TA-7 states the quote correction for a known-words
  command that the pathname-expansion rule alone makes exact, and the
  exemption of a scope-free data command after an unknown directory.

## Impact

- `ShellApprovalMatcher` (`IsScopeFreeDataCommand`), `ShellCommandAnalysis`
  (`GetUnboundedPathnameExpansionWords`), `ShellPolicyCoordinator`
  (`SelectCommandWordsCorrection`), and `ToolCorrection.ShellWordQuoteSuggested`.
- The review catalog and its table, `SubcommandEverywhereGrantTests`, and the
  shell command analysis mutation gate (a new target for
  `IsScopeFreeDataCommand`).
- Security: the exemption applies only to a command with no path scope. The
  correction grants no authority, and the rewritten call passes normal
  approval.
- Operations: no configuration change. The agent sees a correction in place of
  some prompts. The `netclaw-operations` skill describes both rules.
