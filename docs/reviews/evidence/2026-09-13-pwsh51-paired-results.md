# Windows PowerShell 5.1 Paired Consumer Results

Date: 2026-09-13
Updated: 2026-09-14

## Current Candidate Status

The paired code change is ready for maintainer review. This work did not tag,
publish, or deploy a new Netclaw beta.

Netclaw pins official ShellSyntaxTree `0.4.0-beta.1`.
This evidence does not record a package path or package hash.

An explicit restore against the official package passed.

The tests and replays parsed policy input only. They did not execute a shell
command or a private replay payload.

## Security Result

The pair closes the earlier parser-policy parity defect. Partial diagnostic
syntax supplies denial evidence only. It never supplies approval authority.

Netclaw derives `RequiresExactTreeApproval` from typed ShellSyntaxTree facts.
It does not parse private PowerShell option grammar.

An exact-tree command skips all broader coverage sources:

- reviewed-safe policy
- a session grant
- a stored grant
- a persistent grant

An interactive `Auto` or `Approval` request offers only `Once` or `Deny`.
A headless `Auto` request denies the call. `Deny` mode always denies the call.

Windows PowerShell 5.1 recursive `Get-ChildItem` remains exact-only because
its traversal can follow links. PowerShell 7 recursion can reuse authority
only when ShellSyntaxTree proves traversal without link following.

Unknown, malformed, conflicting, or future tree facts remain exact-only. The
same rule applies to an incomplete root, a root separator, a device UNC root,
and a drive-relative `C:` glob.

The one-time retry parses the exact call again. It repeats the hard-deny and
protected-path checks before it can allow execution.

## Bounded Value Facts

`DynamicSkip` can use only an audited non-path value fact. The argument must
set `IsPath` to false and keep `AuthoredFileSystemValue` as `Unknown`.

The accepted facts are:

- `Exact`
- `FiniteSet`
- `OrderedList` with 2 through 32 non-null elements; duplicate values are valid
- `IntegerRange` with matching typed bounds and no more than 4,096 elements

Conflicting facts, arbitrary data, `Concatenation`, and future enum values do
not qualify for this exception.

The reviewed Windows catalog now includes `Select-Object` and `Get-Process`.
The catalog still grants no authority by itself in an unattended context.

This sanitized family is covered and has a reviewed-safe, reusable result:

```powershell
# PARSE/POLICY ONLY. Do not execute.
Get-Content "C:\WORK\PROJECT\SourceFile.cs" |
    Select-Object -Index (113..145)
```

ShellSyntaxTree proves the file path and a bounded 33-element integer range.
Netclaw still applies the trusted-root and protected-path checks.

## Offline Replay

The private replay used 28 complete inputs and their recorded directories.
Raw inputs stayed outside the repository.

| Result | Released beta.3 baseline | Current pair |
|---|---:|---:|
| Resolved analysis | Not recorded | 10/28 |
| Clean reusable candidate | 1/28 | 2/28 |
| Reviewed-safe eligible | Not recorded | 1/28 |
| Exact-tree-only | Not recorded | 2/28 |
| Denied | Not recorded | 0/28 |
| Modeled no-grant Approval prompt | Not recorded | 27/28 |

The baseline matcher produced one clean, candidate-bearing input out of 28.
It classified the other 27 inputs as complex.

The four sanitized shapes produced this comparison:

| Result | Released beta.3 baseline | Current pair |
|---|---:|---:|
| Resolved analysis | Not recorded | 4/4 |
| Clean reusable candidate | 0/4 | 3/4 |
| Reviewed-safe eligible | 0/4 | 3/4 |
| Exact-tree-only | Not recorded | 1/4 |
| Denied | Not recorded | 0/4 |
| Modeled no-grant Approval prompt | Not recorded | 1/4 |

The matcher replay is not a complete prompt simulation. Live logs prove that
all four sanitized shape families caused prompts before this change. Actor
tests prove the current reviewed-safe and exact-only outcomes.

## Verification

| Gate | Result |
|---|---|
| Explicit package restore | Passed |
| Netclaw Security | 1,153/1,153 passed |
| Netclaw Actors | 3,990 passed; 6 platform skips; 3,996 total |
| Netclaw Configuration | 634/634 passed |
| Release build | Passed with 0 errors and one pre-existing `ASPIRE010` warning |
| Focused security mutation checks | 79/79 killed; 0 survived; about 6 minutes |

The mutation checks cover diagnostic denials, tree decisions, root
correspondence, bounded values, candidate projection, approval modes, path
facts, and reviewed-safe policy.

## Historical Candidates

The unpublished `0.3.5-pwshfatigue.4` package was unsafe. It could lose a
nested recursive hard deny after a later unsupported mutation changed the
analysis to diagnostic-only.

The paired denial-only repair closed that defect. The official `0.4.0-beta.1`
package also adds the typed tree and bounded-value facts that reduce common
approval fatigue without broader authority.

The old `.4` experiment is historical context only. No patch or private
payload is included in this evidence set.

## Adoption Status

The rebased branch passed the recorded build, suite, mutation, privacy, and
replay gates against official ShellSyntaxTree `0.4.0-beta.1`.

No new Netclaw beta was cut. A later release needs a separate instruction and
must keep the reviewed package pin and pass the normal release gates.
