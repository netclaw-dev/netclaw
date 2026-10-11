## Why

Source PRD IDs: `PRD-002` SEC-003, SEC-006, SEC-008, and SEC-009.

The approval taxonomy plan changed shell approval behavior in two stacks of
PRs. Stack 1 is #2314 to #2319. Stack 2 is #2321 to #2323, with the
follow-up #2329 and the related PR #2327. Some of these PRs changed the
`tool-authorization` specification, but most did not. This change makes the
specification agree with the merged behavior and the owner decisions D1, D2,
D3, D5 (option A), and D6.

## What Changes

This change is a specification catch-up only. It does not change code.

- Safe phrases: in an interactive run, a reviewed safe phrase covers each path
  that the audience may read with a file tool. An unattended run keeps the
  session and project roots (#2314).
- Data-position rule: a dynamic operand of `echo`, `printf`, `:`, `true`, or
  `false` is data (#2315). The current specification already states this
  rule. This change adds the owner and the examples.
- Multi-line operand: a path word with a control character gets the scope of
  its clean text (#2316).
- Non-path operands: an absolute word below a top-level directory that does
  not exist on the host has no path scope. A grant keeps its folder scope
  (#2317, decision D3).
- Per-command judgment: each unresolved command is one exact candidate.
  Decision D1 lets a safe phrase or a grant for anywhere cover an unknown
  operand. The accepted D1 gap is now stated (#2318).
- Kill deny: a kill is hard-denied only when an operand names the Netclaw
  daemon (#2319, decision D2).
- Glob path facts: a glob word gets its covering directory as its scope. A
  glob that can match a protected path or the credential store gets the
  literal-path denial. The check is lexical (#2321, decision D5 option A). The
  accepted link gap is stated.
- Variable values: the path checks and the hard-deny list check each proved
  value of a variable (#2321).
- Each command inside `if`, `case`, `while`, `until`, and `&` gets its own
  decision (#2321).
- Legacy grants: a legacy grant matches its own command words (#2322).
- Config reads: a file tool can read `netclaw.json` and `tool-approvals.json`.
  Secrets, keys, and the other credential and runtime files stay read-denied
  (#2323, decision D6).
- Bracket-word rule: only a bracket pattern in the program word stays
  unresolved. Brace and regex text keep their earlier decision (#2329).
- Approval note: an approved tool result names the consent answer (#2327).

In scope: the merged behavior above.

Out of scope:

- Approval taxonomy PR 5 of the follow-up plan (unattended runs use the same
  audience policy). It is not merged.
- PowerShell parity for the October changes (#2332).
- Wrapper commands such as `timeout` (#2331).
- SST constructs that still do not parse (SST #227).

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-5 (hard deny), TA-6 (path access and protection),
  TA-7 (shell analysis), TA-8 (coverage), and TA-10 (consent prompts and the
  approval note) change to match the merged behavior.

The `session-cwd` capability does not change. The `cd` proof now compares glob
facts by value, but its observable behavior stays the same.

## Impact

- Code: none. The behavior is in `src/Netclaw.Security/ShellCommandAnalysis.cs`,
  `src/Netclaw.Security/IToolApprovalMatcher.cs`,
  `src/Netclaw.Security/ShellCommandPolicy.cs`,
  `src/Netclaw.Security/ShellGlobScope.cs`,
  `src/Netclaw.Security/ToolPathPolicy.cs`,
  `src/Netclaw.Security/ApprovalPatternMatching.cs`,
  `src/Netclaw.Actors/Tools/PathAccessPolicy.cs`,
  `src/Netclaw.Actors/Tools/ReviewedSafeShellPolicy.cs`,
  `src/Netclaw.Actors/Tools/ShellPolicyCoordinator.cs`,
  `src/Netclaw.Actors/Authorization/Consent/ConsentAnswer.cs`, and
  `src/Netclaw.Daemon/Configuration/DaemonToolPathPolicyFactory.cs`.
- Security: the specification now records each security boundary that the
  stacks changed, with a positive and a negative example. It also records two
  accepted gaps: the D1 unknown operand and the D5 link below a covering
  directory.
- Operations: none. The runbook `docs/runbooks/tool-approval-gates.md` and the
  `netclaw-operations` skill already describe the behavior.
