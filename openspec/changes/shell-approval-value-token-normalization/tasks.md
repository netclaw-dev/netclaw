# Tasks: Shell Approval Value-Token Normalization

Implementation landed on branch `fix/shell-approval-version-arg-normalization`
(PR #1388, commits `5e28920c` + `2755035f`) before this change was opened;
tasks below are checked where the work is already done and verified.

## 1. Core Implementation

- [x] 1.1 Add `IsCallSpecificValueToken` (not a flag, not path-shaped, contains
  a digit) to `ShellApprovalMatcher`, replacing and deleting
  `IsBareIntegerToken` and the version-shape predicate
- [x] 1.2 Add `TrimTrailingValueTokens` (trailing-only, one-token floor) and
  apply it in `ExtractCandidatesViaBashParser` (gate candidates)
- [x] 1.3 Apply the same trim and value-termination in `ReconstructClauseText`
  (persisted/display patterns) so both paths normalize identically

## 2. Tests

- [x] 2.1 Parity tests: `git tag v0.4.2` / `git tag 0.4.2` produce identical
  candidate verbs and patterns; standing `git tag` grant approves both
- [x] 2.2 Boundary table: trailing-only (mid-chain `aws s3 ls` untouched),
  all-alpha operands preserved (`git push origin main`), SHAs and range refs
  trimmed, flags retained before a value (`docker run --name test123`)
- [x] 2.3 Full `Netclaw.Security.Tests` (580) and actor approval/dispatch
  suites (311) pass; Slopwatch clean on changed files

## 3. Spec Sync and Closeout

- [x] 3.1 Create delta spec for `tool-approval-gates` "Shell command pattern
  matching" (this change)
- [x] 3.2 Sync delta to `openspec/specs/tool-approval-gates/spec.md`
  (`/opsx-sync`) and commit on the PR branch
- [ ] 3.3 Verify implementation matches artifacts (`/opsx-verify`)
- [ ] 3.4 Archive the change after PR #1388 merges (`/opsx-archive`)
