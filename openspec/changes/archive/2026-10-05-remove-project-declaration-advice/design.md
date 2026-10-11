## Context

See proposal.md for the motivation. `ShellPolicyCoordinator` selected the
project correction after the temporary and one-call directory advice. The
correction applied only when all of these were true:

1. The shell folder was not readable for a reviewed phrase.
2. A project declaration of that folder passed the path access decision.
3. Each candidate became a reviewed phrase after the declaration.
4. `set_working_directory` was exposed and accepted the folder.

## Goals / Non-Goals

**Goals:**

- Remove the correction and each helper that only it used.
- Prove that no production input reaches the correction before the removal.

**Non-Goals:**

- Change `set_working_directory`, the denial hint, or the file-tool
  `SetWorkingDirectory` remediation.
- Fix the reviewed-safe path check for a PowerShell grammar on a Linux host.
  The daemon never builds that pair.

## Decisions

### The correction is dead for each production shell

Schematic flow of the removed check (it omits the earlier screens):

```text
cwd readable by the audience (host path style)?  -> yes: no correction
declaration of cwd allowed?                      -> no:  no correction
```

`PathAccessPolicy` decides a declaration with the `Read` profile and `Read`
protection. With `ReadFiles = All`, a declaration stays inside the trusted
roots, and a read has no root limit. With `ReadFiles = Roots`, both use the
same roots. With `None`, both fail. So an allowed declaration implies an
allowed read, and the first step always ends the check.

The read check needs the shell path style to be the host style. The daemon
selects Bash on Linux and macOS, and PowerShell on Windows. Each pair uses the
host style.

Evidence on the dev revision before the removal:

- A probe through the real `ToolAuthorizer` with Bash on Linux ran 7,488
  decisions. The decisions covered three audiences, attended and unattended
  runs, and 18 folders. No decision returned the correction.
- The same probe with a PowerShell grammar on a Linux host returned the
  correction in 768 decisions. Only that test-only pair reaches it.
- The Actors suite with a tripwire reached the policy check 52 times. Each
  case was PowerShell on a Linux host, and `set_working_directory` rejected
  each one, so the suite never delivered the correction.

Alternative: keep the correction for a future shell. Rejected. Dead advice
code adds review cost, and a future shell needs a new design anyway.

### The removal also drops two dead parameters

`EvaluateReviewedShellPath` took a proposed project root and a link-rule
switch. Only the removed check used them. The link rule is now always
`IncludingRoot`, which was the value of every remaining caller.

## Risks / Trade-offs

- [A PowerShell test on a Linux host changes] → No test asserts the
  correction, and the corpus probe uses the project folder, so no outcome
  changes. The authorization corpus must show zero differences.
- [A mutation gate anchor named a removed parameter] → Move the anchor to the
  method name. The span and the expected count stay the same.

## Migration Plan

None. The change removes advice only. A rollback restores the code.
