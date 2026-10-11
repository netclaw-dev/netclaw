## Context

See proposal.md, section Why. The code is merged. This change records it in
the `tool-authorization` specification. The source of each rule is a merged
PR and an owner decision of the approval taxonomy plan (October 3, 2026):

| Rule | PR | Decision |
|---|---|---|
| Safe phrase reads each readable path (interactive only) | #2314 | Plan fix 1 |
| Data-position rule | #2315 | Plan fix 2 |
| Multi-line operand scope | #2316 | Plan fix 3 |
| Non-path operand (API route) | #2317 | D3 |
| Per-command judgment, unknown operand | #2318 | D1 |
| Kill deny names the daemon only | #2319 | D2 |
| Glob facts, D5 lexical check, bound values, control flow, `&` | #2321 | D1, D5 option A |
| Legacy grant matches its own words | #2322 | Plan fix 5 |
| File tools read the grant store | #2323 | D6 |
| Approval note in the tool result | #2327 | Reminder pre-flight D3 |
| Bracket-word rule | #2329 | Follow-up of #2321 |

## Goals / Non-Goals

**Goals:**

- Each merged rule has normative text, an owner, a state lifetime, and a
  positive and a negative example.
- Each example names its catalog case when one exists, so that the
  disposition matrix test proves it.

**Non-Goals:**

- No code change and no test change.
- No text for the unmerged PR 5 (unattended runs use the same audience
  policy).
- No PowerShell parity text beyond the current behavior.

## Decisions

### Decision ownership

| Decision or data | Owner | Lifetime |
|---|---|---|
| Hard-deny rules, daemon kill pattern, bound-value check | `ShellCommandPolicy` | Rules process-local; result call-local |
| Bound values and loop values | ShellSyntaxTree through `ShellCommandAnalysis` | Call-local |
| Protected lists (read, write, shell) | `DaemonToolPathPolicyFactory`, `ToolPathPolicy` | Process-local |
| D5 glob match | `ToolPathPolicy.GlobMayReachDeniedPath`, `ShellGlobScope` | Call-local |
| Glob scope and link walk | `ShellGlobScope` | Call-local |
| Candidates, scopes, API-route and control-character rules, bracket-word rule | `ShellCommandAnalysis`, `ShellApprovalMatcher` | Call-local |
| D1 coverage | `ShellPolicyCoordinator`, `ReviewedSafeShellPolicy` | Call-local |
| Safe-phrase path rule | `ReviewedSafeShellPolicy`, `PathAccessPolicy.IsReadableInInteractiveRun` | Call-local |
| Legacy grant match | `ApprovalPatternMatching` | Call-local; grants durable in `ToolApprovalStore` |
| Approval note | `ConsentAnswerCodec.AppendResultNote`, `SessionToolExecutionPipeline`, `SubAgentActor` | Call-local tool result; answer durable in the journal |

The main specification keeps its Decision Owners table. Each modified
requirement also names its owner, so that a reader of one requirement sees
the owner and the lifetime.

### Flow of the checks (schematic)

The pseudocode is schematic. It omits logging, trace rows, corrections, and
the launch checks.

```text
analysis = ShellSyntaxTree(call)                         call-local
screen hard deny on the complete parse,
  on each proved value, on each loop value               call-local
  partial parse -> screen each list element again
screen protected paths: literal words, bound values,
  glob segments (lexical, D5)                            call-local
for each command:
  resolved   -> candidate from command words and scope
  unresolved -> one exact candidate (Once, Deny)
cover each candidate:
  one-time | chat grant (actor-local) | stored grant (durable)
  | safe phrase (interactive: readable path; unattended: roots)
  | D1: interactive, unknown operand only, safe phrase or grant for anywhere
outcome
```

### Why the specification states the accepted gaps

The D1 gap and the D5 link gap are security trade-offs that the owner
accepted. A reviewer must see them in the specification, not only in the
plan page. The alternatives were:

- D5 option B (walk the disk for each glob). The owner rejected it, because
  the lexical check covers each case that an agent can make.
- D1 "safe phrases only". The owner chose the wider rule (safe phrase or a
  grant for anywhere).

### Why D6 text follows the code, not the plan

The plan says that the read deny shrinks to `keys/` and `secrets.json`. PR
#2323 kept more files read-denied (webhook secrets, `daemon.env`,
`devices.json`, `hard-deny-overrides.json`, bootstrap state, the database,
and process-control files). The specification states the merged behavior.

## Risks / Trade-offs

- [The text can drift from the catalog] → Each example names its catalog
  case. The disposition matrix test and the outcome direction check protect
  those rows.
- [#2329 is the last PR of the set] → The bracket-word text matches #2329.
  If #2329 does not merge, the specification overstates the brace case.
  Mitigation: merge this change only after #2329 merges.
- [The main specification has a Verification Map] → The sync step adds the
  new mutation targets (`GlobPolicyMutationTests`,
  `PerCommandJudgmentMutationTests`) to the map rows of TA-5, TA-6, and TA-7.

## Migration Plan

None. The behavior is already in production code.

## Open Questions

- `hard-deny-overrides.json` holds configuration with no secret. It stays
  read-denied. A later owner decision can make it readable. That change does
  not affect this specification change.
