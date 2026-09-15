# Windows Shell Approval Fatigue Assessment

Date: 2026-09-13
Updated: 2026-09-14
Assessed release: `0.27.0-beta.3`
Assessed commit: `a29de86`

## Result

The coordinated code change is ready for maintainer review. This work did not
tag, publish, or deploy a new Netclaw beta.

Netclaw pins the official ShellSyntaxTree `0.4.0-beta.1` package.
This branch does not record a package path or package hash.

This pair closes the earlier diagnostic parity defect. Partial diagnostic
syntax remains denial-only and never supplies approval authority.

The earlier unpublished `0.3.5-pwshfatigue.4` package was unsafe. Its
implementation artifacts are not part of this branch and must not be released.

## Observed Baseline

The Windows host produced 70 prompts across 157 shell authorization attempts.
The prompt incidence was 44.6 percent for the observed window.

The operator selected `Once` for 62 prompts, or 88.6 percent. The operator
saved five durable grants and one session grant.

The selected native host was Windows PowerShell 5.1. PowerShell 7 was absent,
and the startup probe recorded `PreferredHostNotFound`.

| Authorization path | Attempts |
|---|---:|
| Allowed without a prompt | 87 |
| Prompted, then allowed | 68 |
| Prompted, then denied | 2 |
| Total | 157 |

The evidence set contained 28 complete private payloads. It also contained 40
logger previews and two calls without recoverable command text.

Raw inputs, identities, paths, and approval records stay outside the
repository. The review did not execute any captured command.

## Current Policy Boundary

ShellSyntaxTree owns syntax, typed values, and file-tree facts. Netclaw owns
policy, path authority, approval modes, and grant scope.

Netclaw derives `RequiresExactTreeApproval` from the typed facts. It does not
parse a private PowerShell command or option grammar.

An exact-tree command skips these broader authority sources:

- reviewed-safe policy
- a session grant
- a stored grant
- a persistent grant

An interactive `Auto` or `Approval` call offers only `Once` or `Deny`.
A headless `Auto` call denies the request. `Deny` mode denies the request.

Windows PowerShell 5.1 recursive `Get-ChildItem` remains exact-only. Its
recursive traversal can follow links. PowerShell 7 can reuse authority only
when ShellSyntaxTree proves traversal without link following.

Unknown, malformed, conflicting, or future tree facts remain exact-only. A
root separator, an incomplete root, a device UNC root, and a drive-relative
`C:` glob also remain exact-only.

The exact one-time retry parses the command again. It repeats the hard-deny
and protected-path checks before it can allow the request.

## Audited Dynamic Values

`DynamicSkip` can ignore only an audited non-path value fact. The argument
must set `IsPath` to false. Its `AuthoredFileSystemValue` must be `Unknown`.

The accepted value domains are:

- `Exact`
- `FiniteSet`
- `OrderedList` with 2 through 32 non-null elements; duplicates are valid
- `IntegerRange` with matching bounds and no more than 4,096 elements

Conflicting facts, arbitrary values, `Concatenation`, and future enum values
remain dynamic for this decision.

## Reviewed Windows Shapes

The reviewed Windows catalog adds `Select-Object` and `Get-Process`. These
verbs still need the normal policy and path checks.

This sanitized family now has reviewed-safe and reusable coverage:

```powershell
# PARSE/POLICY ONLY. Do not execute.
Get-Content "C:\WORK\PROJECT\SourceFile.cs" |
    Select-Object -Index (113..145)
```

ShellSyntaxTree proves one file path and a bounded 33-element integer range.
Netclaw checks the trusted root and each protected path before coverage.

## Offline Replay Result

The exact replay parsed all 28 private inputs with their recorded directories.
It did not run a command or apply a grant snapshot.

| Result | Released beta.3 baseline | Current pair |
|---|---:|---:|
| Resolved analysis | Not recorded | 10/28 |
| Clean reusable candidate | 1/28 | 2/28 |
| Reviewed-safe eligible | Not recorded | 1/28 |
| Exact-tree-only | Not recorded | 2/28 |
| Denied | Not recorded | 0/28 |
| Modeled no-grant Approval prompt | Not recorded | 27/28 |

The beta.3 matcher classified one input as clean and candidate-bearing. It
classified 27 inputs as complex.

The repository contains four sanitized test shapes. Their replay produced this
comparison:

| Result | Released beta.3 baseline | Current pair |
|---|---:|---:|
| Resolved analysis | Not recorded | 4/4 |
| Clean reusable candidate | 0/4 | 3/4 |
| Reviewed-safe eligible | 0/4 | 3/4 |
| Exact-tree-only | Not recorded | 1/4 |
| Denied | Not recorded | 0/4 |
| Modeled no-grant Approval prompt | Not recorded | 1/4 |

The matcher replay is not a complete prompt simulation. Live logs prove that
all four shape families prompted before the change.

Actor tests prove the current results. Three sanitized shapes use
reviewed-safe policy. The Windows PowerShell 5.1 recursive shape offers only
`Once` or `Deny`.

The private replay predicts a small change in this captured workload. It does
not prove a new live prompt rate. A new deployed observation window must supply
that measurement.

## Security Repair Result

The partial diagnostic consumer adds hard-deny evidence only. It cannot create
a candidate, a pattern, reviewed-safe coverage, or a reusable grant.

The repaired consumer preserves a known static parameter name when its inline
value remains unknown. It denies the dangerous recursive form before the
approval service receives contact.

This repair closes the defect that blocked the `.4` package. The typed beta
tree facts also preserve strict behavior for link-following recursion.

## Verification Record

| Gate | Result |
|---|---|
| Explicit official package restore | Passed with ShellSyntaxTree `0.4.0-beta.1` |
| Netclaw Security | 1,153/1,153 passed |
| Netclaw Actors | 3,990 passed; 6 platform skips; 3,996 total |
| Netclaw Configuration | 634/634 passed |
| Release build | Passed with 0 errors and one pre-existing `ASPIRE010` warning |
| Focused security mutation checks | 79/79 killed; 0 survived; about 6 minutes |

The mutation gate covers denial-only syntax, tree decisions, root facts,
bounded values, candidate projection, approval modes, and reviewed-safe
policy.

## Preserved Constraints

The current pair preserves these rules:

- A hard-denied command never receives an approval option.
- A protected path never receives approval authority.
- Each complete command occurrence receives a separate policy decision.
- Unknown and dynamic command identities remain one-time-only.
- A stored phrase matches complete parser tokens.
- A folder grant does not expand beyond its resolved root.
- A child agent receives no broader authority than its context permits.
- A parser, fact, or store fault fails closed.
- A `Once` response does not create a reusable grant.

## Historical Status

The `.4` diagnostic candidate repaired an earlier nested static-deny loss, but
it introduced a parity defect for an unknown inline recursive value. Released
ShellSyntaxTree `0.3.5` denied that shape, while the `.4` pair could allow it.

The paired repair removes that release block. The official beta also exposes
bounded non-path values and file-tree traversal facts.

The current evidence record is
[`2026-09-13-pwsh51-paired-results.md`](evidence/2026-09-13-pwsh51-paired-results.md).

## Delivery Decision

The rebased branch passed its deterministic gates against the official
ShellSyntaxTree `0.4.0-beta.1` package. The code is ready for maintainer review.

No new Netclaw beta was cut. A later release needs a separate instruction and
must use the normal Netclaw release gates.

A later live window can measure the real prompt change. The offline replay does
not authorize a prompt-rate claim.
