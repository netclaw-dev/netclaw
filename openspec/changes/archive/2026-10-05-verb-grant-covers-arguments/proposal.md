## Why

Since https://github.com/netclaw-dev/netclaw/pull/2306, a shell grant covers
only a call whose command words equal the grant words. A plain argument after
the verb is a command word, so each new package name, remote, or branch gives a
new prompt. On 2026-10-05 the owner decided that a verb grant covers its
arguments (PRD-002 SEC-003, SEC-009).

## What Changes

- A shell grant of two or more command words names a verb. It covers each
  candidate whose command words start with the grant words. The later words
  are the arguments of the verb.
- A shell grant of one word names only the program. It stays exact: a `gh`
  grant covers `gh --help`, not `gh auth logout`.
- `TokenPrefix` and `LegacyExact` grants use the same rule for their words.
- Store hygiene (the save-time skip and the doctor "covered" finding) uses the
  same rule as the approval matcher.
- A word after the verb slot that names a link stays a command word. Its link
  path is also a path scope, so a verb grant never hides a link target from
  the trusted-root and protected-path checks.
- Saving does not change. A new grant saves the words of the approved call.

In scope: the shell grant match, the store hygiene rule, and the link scope.
Out of scope: the save rule, the prompt options, non-shell tools, and any
command-specific knowledge.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-8 states the grant reach rule for both stored
  kinds, the link path scope, and the hygiene rule.

## Impact

- Code: `ToolApprovalEntryComparer.CoversCommandWords` (new shared rule),
  `ApprovalPatternMatching`, `ApprovalGrantHygiene`, `ShellGrantFileWords`,
  and `ShellApprovalMatcher.ProjectCommandWords`.
- Security: a verb grant now covers more calls. A grant word is never free,
  and a program-only grant stays exact, so `gh` still does not cover
  `gh auth logout`. Path, protected-path, trusted-root, hard-deny, audience,
  and scope checks do not change. A link word gets a path scope, so the link
  target gets the same checks as a `./link` path.
- Operations: fewer prompts for repeated verbs. `netclaw doctor --fix` can
  remove more grants, because a short verb grant covers longer grants. The
  CLI help, the runbook, the architecture document, and the
  `netclaw-operations` skill describe the rule.
