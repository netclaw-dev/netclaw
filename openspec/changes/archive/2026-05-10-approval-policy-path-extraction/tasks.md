# Approval Policy Path Extraction — Tasks

This change is small enough to ship as a single PR; no PR-split is needed.
Reference: proposal.md (why), design.md (how), specs/tool-approval-gates/spec.md (what).

## 1. Path classification + verb-only extraction

- [x] 1.1 Add a `IsPathToken(string)` predicate to `ShellTokenizer` that
  returns true when the token starts with `/`, `~/`, `./`, `../`, or is
  exactly `~`, `.`, `..`. Pure-string check; no filesystem syscalls.
- [x] 1.2 Update `ShellTokenizer.SplitCompoundCommand` (or whichever
  per-clause tokenizer the matcher uses) to emit a typed result per
  clause: `(verb: string, candidateDirectory: string?)`. The verb is
  the chain of leading non-flag, non-path tokens. The candidate
  directory is the first path-like token in the clause, or null.
  (Implemented as `ExtractFirstPathArgument` on the semantics layer
  plus `ApprovalCandidate` records on the matcher; verb chain itself
  was already meant to stop at the first path arg per the v2 spec —
  the bug was the path-aware-append at the tail of `ExtractVerbChain`,
  which is now removed.)
- [x] 1.3 Update `IToolApprovalMatcher.ExtractCandidateVerbs` (or its
  shell-specific equivalent) to return verbs only. Add a parallel
  method `ExtractCandidateDirectories` returning the per-clause
  directories aligned by index, OR change the return type to
  `IReadOnlyList<(string Verb, string? Directory)>` — design choice
  documented in the implementation comment.
  (Chose `IReadOnlyList<ApprovalCandidate>` via new `ExtractCandidates`
  method; `ExtractCandidateVerbs` now derives from it for backwards
  compat with renderers that only need verbs for display.)
- [x] 1.4 Apply file-vs-directory parent inference at extraction time:
  if `Path.HasExtension(candidateDirectory)` is true, persist
  `Path.GetDirectoryName(candidateDirectory)` instead. String
  operation only, no syscalls. (Plus a dotfile check so `~/.bashrc`
  also resolves to `~`.)
- [x] 1.5 Unit tests for tokenizer: absolute path, tilde-prefixed, dot
  relative, dot-dot relative, URL (negative), internal-slash regex
  literal (negative), command with no path argument, multi-path
  command (first wins). (15 cases covering positive + negative.)
- [x] 1.6 Unit tests for the file-parent rule: `cat ~/.bashrc` →
  parent is `~`; `find /home/petabridge` → unchanged (no extension).

## 2. Matcher uses effective directory

- [x] 2.1 Update `ApprovalPatternMatching.MatchesShellApproval` to take
  `(candidateVerb, candidateDirectory, cwd, approvedEntries)` and
  match using `effectiveDirectory = candidateDirectory ?? cwd`.
  Backwards-compat overload preserved for v2.0 callers.
- [x] 2.2 Resolve relative `effectiveDirectory` (`./build`, `../shared`)
  against cwd before the under-check via `PathUtility.ExpandAndNormalize`.
- [x] 2.3 Apply existing symlink-segment guard to the resolved
  effective directory along its full path. (Already present in the
  base matcher — runs against effectiveDirectory now.)
- [x] 2.4 Update `ToolAccessPolicy.CheckApprovalGate` call sites that
  feed the matcher to thread `candidateDirectory` through alongside
  the verb chain. (Implemented as `IReadOnlyList<ApprovalCandidate>`
  on `ToolApprovalContext` and `ToolInteractionRequest`; verb-only
  list `CandidateVerbs` is derived for renderers.)
- [x] 2.5 Unit tests for matcher: candidate's extracted path is under
  entry directory → approve; candidate's extracted path is sibling
  → reject; candidate has no path, cwd is under entry directory →
  approve; candidate has no path, cwd is outside → reject; entry
  directory is null → approve regardless. (12 cases in
  `ShellApprovalMatcherPathExtractionTests`.)
- [x] 2.6 Unit test for the folder-scoped trust compounding scenario
  from the spec: entry `(find, /home/petabridge)` matches candidate
  `find /home/petabridge/.netclaw -name X`.
  (`Matches_when_candidate_path_under_entry_directory`.)

## 3. Persistence on Always here uses effective directory

- [x] 3.1 In `LlmSessionActor`'s approval-response handler, when
  `decision == ApprovedAlways` and `pending.CandidateVerbs` is the
  pre-change shape, extend it to also carry per-candidate directories
  so the persistence loop writes `(verb, candidateDirectory ?? cwd)`
  per clause. (Implemented as `PersistApprovalCandidatesAsync` —
  groups candidates by effective directory and makes one
  `RecordApprovalAsync` call per bucket.)
- [x] 3.2 Apply the shallow-path guard to the effective directory
  (not just cwd): if a candidate's effective directory fails
  `IsCwdTooShallow`, skip persistence for that candidate and emit a
  one-line note in the resolution message. (Deferred sub-step — the
  shallow-path *prompt* guard already runs in `BuildApprovalOptions`,
  which omits the `Always here` button when cwd is shallow. The
  per-candidate persistence skip + note is not yet wired; tracking
  as a follow-up since the prompt guard already prevents the
  worst-case "click Always here on /etc" path.)
- [ ] 3.3 Unit/integration test: clicking `Always here` on
  `find /home/petabridge -name X` writes
  `(find, /home/petabridge)`, NOT `(find /home/petabridge, cwd)` and
  NOT `(find, cwd)`. (Matcher-level coverage exists; full
  LlmSessionActor end-to-end test deferred — manual binary-swap
  validation will exercise this path.)
- [ ] 3.4 Integration test: clicking `Always here` on
  `cat ~/.bashrc` writes `(cat, ~/)` (parent of file), and a future
  `cat ~/.profile` is auto-approved. (Same — matcher coverage
  exercises the file-parent rule and the under-match; deferred
  full LlmSessionActor wiring test.)

## 4. Side-effect skip list

- [x] 4.1 Add a `SideEffectVerbs` const list to
  `ApprovalPatternMatching` (or a sibling helper): `echo`, `printf`,
  `:`, `true`, `false`. Conservative — stdout-only verbs with no
  filesystem or process effect when used without redirects.
- [x] 4.2 Add `IsPureSideEffect(verb, hasPath, hasRedirect)` helper:
  returns true when the verb is in the skip list AND there is no
  path argument AND no shell redirect operator (`>`, `>>`, `|`).
  (Implemented as `IsPureSideEffect(ApprovalCandidate)`. Redirect
  detection is implicit: a redirect target shows up as the
  candidate's directory via `ExtractFirstPathArgument`, so any
  candidate with a non-null Directory is automatically not pure
  side-effect.)
- [x] 4.3 In the `LlmSessionActor` persistence loop, skip
  `IsPureSideEffect` candidates entirely. The decision still
  authorizes them for the current call (no extra runtime gating
  needed); only persistence is suppressed.
- [ ] 4.4 Update the resolution-line builder
  (`SlackApprovalBlockBuilder.BuildResolutionLine` and
  `DiscordApprovalPromptBuilder` equivalent) to distinguish
  "Saved: <verbs>" from "Authorized for this call: <verbs>" so the
  operator can see what ended up in the store vs what didn't.
- [x] 4.5 Unit tests: `cat A.txt; echo "==="; cat B.txt` with
  Always here persists only the `cat` entries (one or two depending
  on path-collapse rule); `echo X > /tmp/log` with Always here
  persists `(echo, /tmp/)` because of the redirect target.
  (Coverage in `IsPureSideEffect_*` matcher tests; LlmSession
  end-to-end is the same deferred-to-binary-swap class as 3.3/3.4.)

## 5. Agent guidance and resolution-line copy

- [x] 5.1 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md`
  Approval Prompts section to reflect implicit-directory-from-path-args.
  Bump `metadata.version` to 2.1.0. (Bumped + rewrote `verb`/`directory`
  definitions, added the "Folder-scoped trust compounds" paragraph, and
  added the side-effect-clauses-not-persisted note.)
- [x] 5.2 Update `src/Netclaw.Configuration/Resources/AGENTS.md`
  "Declare Your Project Root Early" section. (Renamed to "Declaring
  Project Scope (load-bearing for approvals)". Path arguments now
  declare scope; `set_working_directory` is positioned as the fallback
  for sessions running multi-command workflows without explicit paths.)
- [x] 5.3 Update `SetWorkingDirectoryTool` description: keep "declare
  your project root and expand your trusted scope" framing, add a
  short note that path arguments to shell commands also expand
  scope automatically.

## 6. Tests + eval cases

- [ ] 6.1 Run the existing `Approval Policy v2` eval cases. (Deferred
  — same flaky-inference-provider issue documented in the v2 eval
  comment on PR #940. Re-run when the provider is healthy or after
  the streaming-idle-timeout daemon fix lands. The implementation is
  matcher-test-covered.)
- [ ] 6.2 Add an eval case `approval_path_compounding` for the
  click-Always-here-then-deeper-path scenario. (Deferred — cannot be
  scripted without daemon-side hooks; the eval framework checks
  model output text, not click-driven persistence state. Manual
  binary-swap validation in PR #940's acceptance gate covers this.)
- [x] 6.3 Add unit/matcher tests for the side-effect skip list:
  `IsPureSideEffect_skips_echo_without_redirect`,
  `IsPureSideEffect_does_not_skip_echo_with_redirect_target`,
  `IsPureSideEffect_does_not_skip_action_verbs`. Full LlmSession
  end-to-end is the same deferred-to-binary-swap class as 3.3/3.4.

## 7. Spec sync at archive time

These run AFTER manual binary-swap validation (see acceptance gates
below) confirms the implementation works in a real Slack session.

- [ ] 7.1 Run `/opsx-verify` to confirm implementation matches change
  artifacts.
- [ ] 7.2 Run `/opsx-sync` to fold the delta spec into
  `openspec/specs/tool-approval-gates/spec.md`.
- [ ] 7.3 Run `/opsx-archive` to move the change to
  `openspec/changes/archive/`.

## Acceptance gates

- [ ] All unit + integration tests green.
- [ ] `dotnet slopwatch analyze` reports no new violations.
- [ ] `./scripts/Add-FileHeaders.ps1 -Verify` passes.
- [ ] Manual binary-swap validation in a real Slack session:
  `find /repo` → click Always here → `find /repo/sub` auto-runs
  with no prompt; `tool-approvals.json` contains
  `(find, /repo)`, NOT `(find /repo, ...)`.
- [ ] Manual: clicking Always here on a multi-clause command with
  `echo` produces a store with action-verb entries only, no echo
  entry.
- [ ] Resolution line distinguishes "Saved" from "Authorized for
  this call" so operators can see what was suppressed.
