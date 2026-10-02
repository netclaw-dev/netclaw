# Tooling Inventory - Netclaw

## Runtime and Build

- `.NET SDK`: pinned by `global.json` (currently .NET 10 line)
- `dotnet` CLI: build, test, run, restore, local tool execution
- local tools configured in `.config/dotnet-tools.json`
- solution scaffold: `Netclaw.slnx` with `src/Akka.Agents` and
  `src/Netclaw.App`

## Planning and Spec Tooling

- `OpenSpec` CLI: installed and initialized in this repo
  - OpenCode command/skill files generated under `.opencode/`
  - repository artifacts under `openspec/`
- markdown docs under `docs/prd/`, `docs/spec/`, and `docs/ui/`
- RALPH loop infrastructure for iterative implementation
  - `ralph-opencode.sh`, `ralph.sh`
  - local Claude skills under `.claude/skills/`
  - flight recorder at `.ralph/runs/<run-id>/`

## Copyright Header Enforcement

| Command | Purpose |
|---------|---------|
| `scripts/Add-FileHeaders.ps1` | Add Petabridge copyright headers to all `.cs` files |
| `scripts/Add-FileHeaders.ps1 -Verify` | CI: check all files have headers (exit 1 if missing) |
| `scripts/Add-FileHeaders.ps1 -WhatIf` | Preview which files need headers |

## Focused Mutation Tests

All focused gates run in one job definition, `mutation-gates` in `pr_validation.yml`, on each pull request, merge group, and `dev` push.
The job has three Linux matrix groups that run in parallel with the normal test matrix.
Each group runs its gates in sequence after one checkout and tool restore, and it reports every failed gate.
To add a gate, add its script name (`scripts/run-<name>-mutations.sh`) to the lightest group. Do not add a new job.

Focused mutation tests prove that deterministic tests reject a specific unsafe
change at a security or authority boundary. They do not measure general code
coverage. They do not replace positive and negative behavior tests.

### Current Targets

| Target | Protected claim | Expected mutants | Command |
|--------|-----------------|------------------|---------|
| `PathAccessPolicy.AddSessionRoots` | Only a Personal context receives shared session roots | 2 killed | `./scripts/run-path-access-mutations.sh` |
| `ToolAccessPolicy.AdmitMcpAudience` | Server and tool audience grants precede approval | 2 killed | `./scripts/run-tool-authorization-mutations.sh` |
| `ToolAccessPolicy.ScreenHardDeny` | A shell hard denial precedes approval | 1 killed | `./scripts/run-tool-authorization-mutations.sh` |
| `ShellGrantCandidateResult.IsFor` | Approval evidence keeps the requested candidate facts | 1 killed | `./scripts/run-tool-authorization-mutations.sh` |
| `ShellPolicyEvaluation.CandidateState.ValidateActorEvidence` | Actor evidence cannot replace existing candidate coverage (`Coverage != null`) | 1 killed | `./scripts/run-tool-authorization-mutations.sh` |
| `ToolAuthorizer` shell rule order (hard deny, trusted root, covering grant) | No rule can move ahead of an earlier rule: hard deny and today's trusted-root check precede a covering grant | 3 killed | `./scripts/run-tool-authorizer-order-mutations.sh` |
| Shell analysis, denial-only, tree effects, and reviewed-safe gates | Parser-proved regions and authored diagnostic syntax preserve hard denials; only bounded audited non-path values and consistent non-link-following tree facts can use reusable approval | 81 killed | `./scripts/run-shell-command-analysis-mutations.sh` |
| Shell assignment identity, wrapper fallback, wrapper child source, hard-deny screen, syntax reconciliation, host mode, prompt rollback, and Bash sanitation | Reusable grants require exact facts, fallback wrappers and wrappers with an assignment prefix must stay one-time, a wrapper child source is the decoded argument value, unresolved Bash source and each list element meet the hard-deny screen, versioned prompts must fail closed, and strong modes require the reviewed launch contract | 71 killed | `./scripts/run-shell-assignment-mutations.sh` |
| Filesystem authority folder membership, repository identity, repository persistence, and the folder of a new grant | Folder and repository grants require candidate scope, identity, registration, and containment; a folder grant trusts its own root and refuses a link below it; a `..` after a link makes the shell scope unresolved; a new folder grant uses the directory where its occurrence runs | 18 killed | `./scripts/run-approval-directory-mutations.sh` |
| `ReminderManagerActor.HandleExecutionOutcomeAsync` | Only the current attempt can settle; the manager replies after settlement | 2 killed | `./scripts/run-reminder-execution-mutations.sh` |
| `ActiveExecutionTracker.TryRemove` | Only the current owner can remove its guard; cleanup removes that guard | 2 killed | `./scripts/run-reminder-execution-mutations.sh` |
| `McpArtifactMaterializer.TryAdmit` | Scanner approval and verified MIME both precede MCP artifact storage | 4 killed | `./scripts/run-mcp-artifact-admission-mutations.sh` |
| `SkillManageTool.GuardMutationTarget` and the filesystem authority link and protection results | A skill mutation cannot follow a link, write a protected path, or skip the atomic-write temp file | 5 killed | `./scripts/run-skill-manage-guard-mutations.sh` |
| `ApprovalPatternMatching.VerbChainEquals` and `ShellPolicyCoordinator.SelectCommandWordsCorrection` | A shell grant covers exactly its command words; a `gh` grant does not cover `gh auth logout`; Unknown command words get a rewrite correction | 5 detected | `./scripts/run-exact-verb-chain-mutations.sh` |

Run the path-access check locally:

```bash
./scripts/run-path-access-mutations.sh
```

The script tests two mutants in the shared session-root boundary.
The job fails unless both mutants die.
The local prototype took 1 minute 28 seconds after package restore.
A cold CI runner should take two to four minutes.

The harness uses xUnit 2 because Stryker's VSTest adapter does not support xUnit 3 correctly.
The script requires `perl` and `jq`, which the Linux CI image supplies.

### MCP Artifact Admission Gate

Run the MCP artifact admission gate:

```bash
./scripts/run-mcp-artifact-admission-mutations.sh
```

The script selects the two fail-closed checks in
`McpArtifactMaterializer.TryAdmit`. It requires two killed mutants for scanner
approval and two killed mutants for verified MIME presence.

The tests supply inconsistent scanner results on purpose. One result has a
verified MIME with an explicit rejection. The other has approval without a
verified MIME. Neither result can authorize storage.

The source selector rejects a missing or duplicate boundary before Stryker
starts. The gate also rejects a changed mutant count, a survivor, or a compile
error in the selected span.

The local run took 1 minute 40 seconds after package restore. The separate CI
job retains a 10-minute timeout and uploads `mcp-artifact-admission-mutation-report`.

### Tool Authorization Gate

Run the tool authorization gate:

```bash
./scripts/run-tool-authorization-mutations.sh
```

The script selects three conditions in `ToolAccessPolicy`.
Each mutant removes a logical negation.
The script requires one killed mutant at each selected source location and exactly three tested mutants overall.
The gate fails if a target is absent, survives, exceeds its time limit, or cannot compile.
Stryker can report unrelated compiler errors before it applies the source filter.
Those errors do not count as tested mutants.

The tests use the real dispatcher, MCP adapter, and shell policy coordinator.
Local probe tools count calls without an MCP connection or a host process.
Public and Team cases cover both MCP grant layers under Auto and one-time approval.
Personal cases cover shell hard denial under both modes.
Auto cases test forbidden calls before their permitted controls.
Approval cases first prove that the same approval keys permit the call.
Each denial must preserve the probe call count.

These tests preserve PRD-002 SEC-003 and PRD-006 MCP-003.
See [TA-3 (audience admission)](openspec/specs/tool-authorization/spec.md#requirement-ta-3-audience-profiles-admit-tools) and
[TA-5 (hard deny)](openspec/specs/tool-authorization/spec.md#requirement-ta-5-hard-deny-precedes-grant-lookup-and-repeats-at-launch)
in the tool authorization contract.
The gate covers authorization before dispatch. It does not prove MCP transport or native shell containment.

The final local run took 88 seconds after package restore.
The authorization PR 4 re-run killed the same 3 + 2 mutants in about 5 minutes.
The separate CI job retains a 10-minute timeout and uploads `tool-authorization-mutation-report`.
Its report directory is `artifacts/stryker/tool-authorization`.

### Tool Authorizer Order Gate

Run the rule-order gate of the linear authorizer:

```bash
./scripts/run-tool-authorizer-order-mutations.sh
```

`ToolAuthorizer` (authorization PR 6a) states its rule order as one
`decision ??= Rule(call);` line per rule (`decision ??= await RuleAsync(call, ct);`
for a rule that reads the grant store). The script selects three lines of the
shell rule list: hard deny, the trusted-root check, and the covering grant.
Stryker turns `??=` into `=` on each line. The rule then runs after an earlier
decision and replaces it, so the rule moves ahead of every earlier rule. The
script requires one killed mutant on each line and three tested mutants overall.
A missing or duplicated rule line fails before Stryker starts.

`ToolAuthorizerOrderMutationTests` kills the mutants. A covering grant exists in
every case:

- A hard-denied phrase stays denied. A control with a granted phrase is allowed.
- A Team audience stays denied. No later rule can clear an admission denial.

Since authorization PR 6e, the same class also pins the grant-first rule for an
unattended call in Approval mode. A stored grant decides for a path or a working
directory outside every trusted root. The negative controls stay denied: a call
without a grant (the denial names the missing grant), Auto mode, and a protected
path with a grant.

The tests pick the host shell and a temporary root without links, so they also
pass in the normal Windows and macOS test jobs. The local run took about
2 minutes after package restore. CI runs it in the `authorization` group of the
`mutation-gates` job. Its report directory is
`artifacts/stryker/tool-authorizer-order`.

### Exact Verb Chain Gate

Run the exact verb chain gate:

```bash
./scripts/run-exact-verb-chain-mutations.sh
```

The script runs Stryker twice. The first run selects the length check in
`ApprovalPatternMatching.VerbChainEquals`: `!=` to `==` and `return false` to
`return true`. The `==` mutant restores prefix matching for a longer candidate,
so a `gh` grant would cover `gh auth logout`. The second run selects the return
of `ShellPolicyCoordinator.SelectCommandWordsCorrection` (three mutants). A
mutant that drops the rewrite correction turns a bare-glob call back into a
prompt or a denial. `ExactVerbChainMutationTests` and
`ToolAuthorizerOrderMutationTests` must detect all five. A missing or
duplicated span fails before Stryker starts.

The local run took about 1 minute after package restore. CI runs it in the
`approval-and-execution` group of the `mutation-gates` job. Its report
directory is `artifacts/stryker/exact-verb-chain`.

### Approval Directory Gate

Run the approval directory gate:

```bash
./scripts/run-approval-directory-mutations.sh
```

The script reuses the xUnit 2 harness.
It selects eight security source regions, three approval actor conditions, and the folder rule of the grant builder.
Folder containment and link checks are in the filesystem authority
(`src/Netclaw.Security/Authorization/Filesystem`). Bash and PowerShell grants use
the same containment rule and the same link walker, so one containment target
replaces the two shell-specific targets.

| Decision | Expected mutants |
|----------|------------------|
| Folder containment (`FileSystemAuthority`, both shells) | 1 killed: remove the logical negation |
| Folder link rule starts below the grant root (`LinkRule.BelowRoot`) | 1 killed: include the root in the link check |
| Link rejection result | 2 killed: force either conditional outcome |
| Candidate repository scope and identity | 3 killed: force a result or relax the identity check |
| Parent segment after a link, matcher gate (`ShellApprovalMatcher`) | 1 killed: negate the condition |
| Parent segment after a link, link test (`FileSystemAuthority`) | 2 killed: negate the test or include the anchor |
| Common identity across candidates (`RepositoryIdentity`) | 1 killed: remove the logical negation |
| Reciprocal worktree registration (`RepositoryIdentity`) | 1 killed: remove the logical negation |
| Persistence candidate resolution | 1 killed: remove the logical negation |
| Persistence common identity | 1 killed: remove the logical negation |
| Persistence worktree root | 1 killed: remove the logical negation |
| Folder of a new grant (`GrantBuilder`, `candidate.Directory ?? workingDirectory`) | 3 killed: swap the operands or keep only one side |

The script requires these counts at their exact source locations and 18 tested mutants overall (12 Security, 6 Actors).
It fails if a target is absent, survives, exceeds its time limit, or cannot compile.
The source selector rejects an absent or duplicate boundary before Stryker starts.
This protects the gate when the authorization code and diagnostic code contain similar conditions.

Twenty-two cases exercise the approval matcher and persistence gate with real directories and links.
They cover the grant root, normal descendants, sibling prefixes, traversal, relative paths, and candidate scope that differs from cwd.
One case proves that a grant root which is itself a link still covers its children (R3).
One case proves that a `..` after a link voids the grant, and that a `..` after a real directory or below a root alias keeps it.
The repository cases cover candidate resolution, mixed identities, reciprocal registration, and a nested registered worktree.
The link cases prove that the link reaches the sibling directory before they require denial.
Windows path cases cover case rules, drive boundaries, and traversal on every host.
The native filesystem cases select Bash on POSIX hosts and PowerShell on Windows.
The Linux mutation job runs the shared link walker on POSIX links only; the ordinary Windows test job exercises Windows links.

The matcher shares `EvaluateApprovalScope` with `ToolApprovalActor` and shell approval evidence validation.
The three persistence targets are in `ToolApprovalActor.TryCreateEntry`. It reads the repository from `GrantScope.Repository` and the builder's worktree from `ToolApprovalGrant.RepositoryWorktree`.
The grant builder target protects the directory proof of a `cd` list: each folder grant uses the directory of its own occurrence (for example `cd@/work/sub`), and a candidate without a directory uses the call directory, not "everywhere". `Folder_grant_uses_the_directory_where_each_occurrence_runs` kills all three mutants.
These tests preserve PRD-002 SEC-003 and
[TA-8 of the tool authorization contract](openspec/specs/tool-authorization/spec.md#requirement-ta-8-every-candidate-needs-coverage).
They prove folder-grant decisions. They do not prove native process containment or races between authorization and file access.

The final local run took less than four minutes after package restore.
The separate CI job retains a 10-minute timeout and uploads `approval-directory-mutation-report`.
Its report directory is `artifacts/stryker/approval-directory`.
The durable actor report is below its `actor` directory.

### Reminder Execution Gate

Run the reminder execution gate:

```bash
./scripts/run-reminder-execution-mutations.sh
```

The script reuses the xUnit 2 harness and selects four mutations:

| Boundary | Expected mutant |
|----------|-----------------|
| Manager outcome ownership | Reverse the execution-ID comparison |
| Manager acceptance reply | Remove the reply from `finally` |
| Tracker removal ownership | Reverse the execution-ID comparison |
| Tracker guard cleanup | Remove the dictionary removal |

Each location must produce one killed mutant. The report must contain exactly four tested mutants.
The gate fails if a target is absent, ignored, survives, exceeds its time limit, or cannot compile.
The selector rejects absent or duplicate source markers before Stryker starts.
Unrelated compiler errors do not count as tested mutants.

Seven cases use the real manager, execution tracker, actor mailbox, definition store, and history store.
An isolated host supplies the local Akka.Reminders scheduler with an in-memory store.
The fixture observes execution IDs through the existing dispatch event and holds the session pipeline on a task.
Actor replies provide barriers. The tests use no delay or sleep to wait for state.

The stale-message cases complete attempt A, start attempt B, and replay A's completion or termination.
They require B's guard, history, failure count, and alerts to remain unchanged.
A current-termination control must record the failure and release its guard.
Success and failure cases require the correct acceptance ID, history, failure count, and subsequent dispatch.
A directory at the atomic-write path causes a real save failure; cleanup and the acceptance reply must still occur.

These tests preserve PRD-008 SCHED-005 and SCHED-007.
See [the execution and settlement contract](openspec/specs/netclaw-scheduling/spec.md) and
[the history contract](openspec/specs/reminder-execution-history/spec.md).
The fixture injects internal outcome messages and synthetic envelopes.
It does not prove the child outcome producer, durable scheduler settlement, restart recovery, or external delivery.

Stryker 5.0.0 omits statement removal when the statement contains an `out` keyword.
It also filters the complete `finally` deletion when the acceptance-reply mutant exists.
Thus, the cleanup mutant targets the tracker dictionary, not the manager's `TryRemove` call.
An explicit local deletion of that call must also make the cleanup cases fail before this gate changes.

The final local run took 95 seconds after package restore.
The separate CI job retains a 10-minute timeout and uploads `reminder-execution-mutation-report`.
Its report directory is `artifacts/stryker/reminder-execution`.

### Skill Manage Guard Gate

Run the skill_manage guard gate:

```bash
./scripts/run-skill-manage-guard-mutations.sh
```

The script reuses the xUnit 2 harness. `SkillManageTool.GuardMutationTarget`
asks the filesystem authority for the link and protection decisions, so two
targets are in `FileSystemAuthority` (project `Netclaw.Security`) and one is in
`SkillManageTool` (project `Netclaw.Actors`):

| Decision | Expected mutants |
|----------|------------------|
| Link check result | 2 killed: force either conditional outcome |
| Protected-path check result | 2 killed: force either conditional outcome |
| Atomic-write temp file | 1 killed: remove the statement that adds `<target>.tmp` to the checked paths |

The claims did not change. The count changed from three to five because each
authority result is a conditional expression. Stryker tests "always" and
"never" for it, where the earlier `if` call had one negation. The approval
directory gate also covers the shared link result line.
Each location must produce its killed mutants, and each report must contain
exactly that many tested mutants.
The gate fails if a marker is absent or duplicated, a mutant survives, or a mutant cannot compile.

Four cases use a real temp skills tree and the production `DaemonToolPathPolicyFactory` deny list.
A control write must succeed. A write through a linked directory, a write through a link at
`<target>.tmp`, and a flat-file skill write into `.system` must fail and leave outside files unchanged.

The gate omits the inspection-failure branch (`PathDecision.Unverifiable`). No
deterministic test can make the link walker fail yet, so a mutant that allows on
error would survive.
The gate does not prove the absence of a race between the check and the write.

The local run took 1 minute 54 seconds after package restore.
The separate CI job retains a 10-minute timeout and uploads `skill-manage-guard-mutation-report`.
Its report directory is `artifacts/stryker/skill-manage-guard`.

### Shell Analysis Gate

Run the shell analysis gate:

```bash
./scripts/run-shell-command-analysis-mutations.sh
```

The script tests 81 mutants across execution-region accounting, denial-only
matching, tree traversal and root correspondence, bounded non-filesystem
values, bare status-parameter output, candidate extraction, approval mode,
path facts, and reviewed-safe policy. The job fails unless every mutant dies.

Two status-parameter mutants test the rule that only bare `$?` can preserve
reusable candidates. The focused test rejects other unknown output data.
The new target took 49 seconds after package restore.

The script groups targets by source project. Stryker analyzes each source project once.
The local run on 2026-09-24 took under four minutes.

Scope review for authorization PR 4 (shell facts): ShellSyntaxTree now supplies
the approval units, the candidate verbs, and the bundled-wrapper child source.
No marker moved, and the counts did not change (72 Security and 9 Actors
mutants killed; the run took 5 minutes). The raw-text hard-deny scan for
unresolved input moved to `LegacyShellTextScan` without a change to its
algorithm. The owner kept it (2026-09-30), and a parser screen now adds a
hard-deny check of each Bash list element. Neither has a focused target:
`HardDenyParityCorpusTests` pins the kept denials and the four stricter
background-list cases. The gate re-run after the screen killed the same 81
mutants. The cd projection merge re-pointed the `IsMessy` marker to its new
signature (it now takes the link rule); the same 72 Security and 9 Actors
mutants die. CI allows 30 minutes for
hosted-runner variance and report upload. The report directory is
`artifacts/stryker/shell-command-analysis`.

### Shell Assignment Gate

Run the shell assignment gate:

```bash
./scripts/run-shell-assignment-mutations.sh
```

The script tests 71 mutants across twelve narrow boundaries.
It covers grant identity, wrapper fallback, wrapper child source, the hard-deny screen, prompt rollback, reviewed-safe exclusion, source spans, Bash host selection, and environment sanitation.
The prompt rollback target is `ConsentAnswerCodec.IsOffered` in `src/Netclaw.Actors/Authorization/Consent/ConsentAnswer.cs`.
It decides whether the prompt offered the selected option key before the key becomes a `ConsentAnswer`.
The job fails unless every mutant dies.

The local calibration run took about five minutes after package restore.
CI allows 15 minutes for hosted-runner variance and report upload.
The report directory is `artifacts/stryker/shell-assignment`.
The authorization PR 4 re-run killed the same 41 Security and 15 Actors
mutants in about 4 minutes. The wrapper fallback target still covers the
parser-decoded child source.

### Scope Review

Review the target list after each security fix or authority policy change.
Also review it as part of each minor release.

Add one focused target when all these conditions apply:

- The code controls authorization, isolation, privacy, identity, destructive access, or execution ownership and cleanup.
- A plausible mutation represents a specific unsafe behavior.
- Deterministic tests reject that mutation.
- A narrow source span contains the relevant decision.
- Stryker produces stable, meaningful mutants for that span.
- Its matrix group stays below the job timeout and finishes before the Windows test job.

Use this procedure:

1. State the protected claim and the unsafe mutation.
2. Apply the mutation in a disposable worktree.
3. Confirm that the applicable tests fail for the expected reason.
4. Configure Stryker for the smallest source span that contains the decision.
5. Pin the expected mutant count and require each mutant to die.
6. Record the target, claim, count, command, and measured cost in this section.
7. Split the target into a parallel job if the total job approaches its timeout.

Do not add a broad project scan. Broad scans can produce equivalent mutants,
long runs, and invalid results from the current xUnit 3 adapter path.

Review these candidate boundaries before lower-risk code:

1. Additional native-tool and structured-path decisions in `ToolAccessPolicy`.
2. Additional approval scope decisions, including the native Windows link branch.
3. Shell hard-deny decisions in `ShellCommandPolicy`.
4. Slack, Discord, and Mattermost ACL decisions.
5. Device bearer token authentication.

## Approval Contract Tooling

These tools protect the shell approval contract during the tool authorization
consolidation. They need only `python3` (standard library) and `git`.

### Evidence Fixtures

The approval and tool-friction evidence JSON is in
`src/Netclaw.Security.Tests/Evidence/`. Both `Netclaw.Security.Tests` and
`Netclaw.Actors.Tests` read it. Tests must not load files from `openspec/`,
because an archive step moves change folders. The folder README lists each
file and the tests that read it.

### Outcome Direction Check

`scripts/check-approval-outcome-direction.py` compares the `Result` column of
the review snapshot
`src/Netclaw.Actors.Tests/Tools/ShellApprovalDispositionMatrixTests.Shell_approval_cases_match_review_table.verified.md`
with the same file at a baseline revision. It keys rows by section heading and
case ID.

| Change | Rule |
| --- | --- |
| `Allowed` to other | Fails unless an intended change has `approvedBy` and names a negative control. |
| `RequiresAgentCorrection` to `Allowed` | Fails unless an intended change names a negative control. |
| `RequiresAgentCorrection` to `Denied` | Fails unless an intended change has `approvedBy`. |
| `RequiresApproval` to or from `RequiresAgentCorrection` | Passes. Neither outcome runs the call. |
| `Denied` to other | Fails unless an intended change has `approvedBy`. |
| `RequiresApproval` to `Allowed` | Fails unless an intended change names a negative control. |
| `RequiresApproval` to `Denied` | Fails unless an intended change has `approvedBy`. |
| Case removed | Always fails. |
| Case added | Passes. The check reports it. |

A negative control is a case ID in the same section. The case must exist in the
baseline and in the candidate snapshot. It must prompt or deny in both. Only the
owner can give `approvedBy`. A table header that repeats a column name is bad
input.

Owner review: a change to the intended-changes file, to the check script, or to
its CI job always needs owner review. Such a PR is never an automatic merge.

List intended changes in
`src/Netclaw.Actors.Tests/Tools/approval-outcome-intended-changes.json`:

```json
{
  "changes": [
    {
      "section": "Fresh Personal approval matrix",
      "id": "<case ID>",
      "from": "RequiresApproval",
      "to": "Allowed",
      "reason": "<why the change is safe>",
      "negativeControl": "<case ID that still prompts or denies>",
      "approvedBy": "<owner, when the rule needs it>"
    }
  ]
}
```

The check fails when a new entry matches no transition (stale entry). An entry
that is also in the baseline version of the file is history. The check ignores
it, and it does not justify a new transition.

```bash
python3 scripts/check-approval-outcome-direction.py                       # merge base of HEAD and origin/dev
python3 scripts/check-approval-outcome-direction.py --base-ref origin/feature/x
python3 scripts/check-approval-outcome-direction.py --base-rev 2fe42f1b3
python3 -m unittest discover -s scripts/tests -p 'test_*.py' -v          # self-tests
```

Exit status 0 means pass, 1 means a rule violation, and 2 means bad input. The
`Approval Outcome Direction` job in `pr_validation.yml` runs the self-tests and
the check against the base of each pull request.

### Authorizer Differential

Authorization PRs 6a to 6c ran `ToolAuthorizerDifferentialTests` in CI. It
compared the old gate with `ToolAuthorizer` on the catalog and on shell and
tool corpora. PR 6d deleted the old gate, so the in-CI differential has no
reference side. The corpus differential below compares the production path
with `dev` and is the proof for each later slice.

### Authorization Corpus Differential

`scripts/authorization-corpus/run.py` runs a large corpus of tool calls through
the production authorization path of two revisions and compares each decision.
Use it for each slice that moves authorization code. Zero differences against
`dev` is the proof that a refactor keeps every decision.

```bash
python3 scripts/authorization-corpus/run.py --base upstream/dev                 # HEAD against dev
python3 scripts/authorization-corpus/run.py --base upstream/dev --quick         # 3 states, a few minutes
python3 scripts/authorization-corpus/run.py --base upstream/dev --head-adapter authorizer
```

How it works:

1. The script builds the corpus from every string literal in
   `src/Netclaw.Actors.Tests` and `src/Netclaw.Security.Tests` at a fixed
   revision (`--corpus-revision`, default `4244eaed5`), plus
   `extra-commands.txt`. Each literal also runs after four compound prefixes
   (`cd` lists, an external directory, and the temporary root). The default
   corpus has 54,909 shell inputs.
2. For each revision, the script makes a disposable `git worktree`, copies the
   probe (`probe/AuthorizationCorpusProbe.cs`) and one adapter into
   `Netclaw.Actors.Tests`, builds, and runs the probe. The `gate` adapter reads
   `EvaluateAuthorizationResultAsync` (revisions up to authorization PR 6c).
   The `authorizer` adapter reads `ToolAuthorizer`. `auto` picks the
   production path of the revision.
3. The probe evaluates 16 shell states (Bash: 3 grant states, interactive or
   unattended, Approval or Auto; PowerShell 7: 2 grant states, Approval or Auto)
   and 24 tool states (3 audiences, interactive or unattended, 4 consent modes)
   with the 62 tool inputs of the differential test. After a consent request,
   it also evaluates the retry with a "Once" answer. After a tool consent
   request, it records a chat grant and evaluates the call again.
4. The script compares the two outputs and writes a report with the outcome
   transitions and the first differences.

Each line holds the outcome, reason, advice, consent request, matched grants,
coverage trace, store lookups, and the analysis that the process may execute.
The probe replaces run-specific paths with placeholders (`{P}`, `{S}`, `{X}`,
`{R}`, `{T}`, `{REPOSITORY}`, and the GUID of the fake Windows root). The
compare step also replaces the parent of the private temporary root, which a
`..` path can reach. Grant timestamps compare by presence only.

Caution: the probe runs with a private temporary root (`TMPDIR`, `TMP`, and
`TEMP` point into the work directory). The corpus replaces the literal `/tmp`
with that root (`{T}`). A decision therefore never reads the shared `/tmp`,
which other processes change during a run.

Notes:

- The script tests committed revisions only. Commit the work before a run.
- The work directory is `artifacts/authorization-corpus` (`--out` changes it).
  The script reuses an output when the `src/` tree, adapter, probe, corpus,
  and states are the same. A full output is about 600 MB.
- Bash states need POSIX filesystem semantics. On Windows, the probe runs the
  PowerShell and tool states only.
- Exit status 0 means no difference. Exit status 1 means at least one
  difference. A slice with an intended change (for example PR 6e) lists each
  transition from the report.

### Authorization Metrics

`scripts/authorization-metrics.py` reports production lines and declared types
for the tool authorization path in 11 groups. Each group has a file list and
globs for the planned `src/Netclaw.{Actors,Security}/Authorization/` folders.
The script skips listed files that do not exist and reports them. It never
counts test projects. It also reports the total of all production `.cs` files
under `src/`. Code that moves out of the groups to an unlisted path stays in that
total, so read the group delta together with the whole-tree delta. `--compare`
prints a warning for each listed file that is missing at either revision.

```bash
python3 scripts/authorization-metrics.py                          # working tree
python3 scripts/authorization-metrics.py --rev 2fe42f1b3          # one revision, no checkout
python3 scripts/authorization-metrics.py --compare origin/dev HEAD
python3 scripts/authorization-metrics.py --rev HEAD --files       # list each file
```

The baseline at `2fe42f1b3` is 23,873 lines and 257 types in 67 files. The
whole production tree at that revision is 192,146 lines and 2,410 types in 909
files.

## Interactive CLI Smoke Tests (Tape Harness)

The native smoke harness exercises the interactive Termina TUI surface
that the non-interactive scenarios cannot reach (Spectre-style prompts,
wizard flows, model/provider/webhook TUIs). It drives the **real native
binary** — no Docker. Tape bodies live at `tests/smoke/tapes/<name>.tape`;
sibling assertion scripts at `tests/smoke/assertions/<name>.sh` validate
the artefacts each tape produced. The same `run-smoke.sh` entrypoint runs
in CI (`smoke.yml`) and locally — agents working on
TUI code SHOULD run the harness before declaring a change done.

| Command | Purpose |
|---------|---------|
| `./scripts/smoke/run-smoke.sh light` | PR-gating subset: all flow tapes + non-interactive scenarios |
| `./scripts/smoke/run-smoke.sh full` | Full suite (placeholder: identical to light until backfilled) |
| `./scripts/smoke/run-smoke.sh <name>` | Single tape or scenario, e.g. `init-wizard` (fastest inner loop) |
| `./scripts/smoke/run-smoke.sh skill-sync` | Live daemon and RFC feed proof for immediate skill updates |
| `./scripts/smoke/run-smoke.sh screenshots` | Screenshot regression: capture + byte-compare against baselines |
| `./scripts/smoke/install-vhs.sh` | Idempotent VHS install (Linux/x86_64 + macOS via Homebrew) |

`run-smoke.sh` publishes the binary (or uses `NETCLAW_SMOKE_CLI` /
`NETCLAW_SMOKE_DAEMON` if exported), installs `vhs`, starts a native
`ollama serve`, and pulls the smoke models automatically.

Config-writing flow tapes (`init-wizard`, `provider-add`, `provider-rename`,
and `config-*`) must have executable semantic assertion scripts under
`tests/smoke/assertions/`. `run-native-tape.sh` fails these tapes when the
assertion is missing or non-executable.

The `skill-sync` scenario starts a mutable local RFC feed and the published
daemon. It runs `netclaw skill sync` twice and changes the feed between passes.
It checks the exact resource SHA-256, the live `/api/skills` inventory, and an
unchanged `netclaw.json` file. The scenario does not call a model.

When a tape fails, `smoke-logs/tapes/<name>/` collects: a debug GIF of the
last frame, the combined tape file, daemon logs, and the produced
`NETCLAW_HOME`. CI uploads the `smoke-logs` directory as a job artefact.

**Rerun this workflow in full, never with `--failed`.** The binary artefact
name ends with the attempt number
(`netclaw-native-binaries-linux-x64-<run-id>-1`). A partial rerun becomes
attempt 2 and looks for `...-2`, which the publish job never produced, so
`Download binary artifact` fails before a tape runs. Use
`gh run rerun <run-id>`, not `gh run rerun --failed <run-id>`.

The job installs Ollama from its release-pinned installer before it runs a
tape. A `curl: (35) Recv failure` at that step is a network failure that
reaches no test code. Confirm the failure is inside a tape before you
investigate the change under review.

**Authoring conventions are in `tests/smoke/tapes/README.md`**
— the short version: `Wait+Screen /pattern/` only (no `Sleep`), 1400×800
default surface, no `Screenshot` directives in flow tapes, pair every
non-trivial tape with an assertion script that re-validates `netclaw
doctor` and the
relevant `--json` output.

## Demo AppHost Smoke Test (Slow)

`samples/Netclaw.Demo.AppHost.IntegrationTests` is an Aspire-driven
end-to-end test that boots the demo AppHost (`samples/Netclaw.Demo.AppHost`),
waits for every resource — Mattermost container, Ollama container,
`qwen3.5:2b-q4_K_M` model, NetClaw daemon project — to reach healthy, posts a
Mattermost message via REST as the seeded test user, and asserts the
wiring routes the message through.

Opt-in by design: the test self-skips unless
`NETCLAW_RUN_DEMO_SMOKE=1` is set (same pattern as the Mattermost
integration tests). The `[Trait("Category", "SlowSmoke")]` trait is a
secondary filter for local-dev runs. A bare `dotnet test` on any CI
runner therefore reports the test as skipped, not failed. Invoke with:

```bash
NETCLAW_RUN_DEMO_SMOKE=1 \
  dotnet test samples/Netclaw.Demo.AppHost.IntegrationTests \
    --filter Category=SlowSmoke
```

Prerequisites: Docker daemon reachable, ~4GB of disk free on a cold
cache (Mattermost preview + Ollama image + `qwen3.5:2b-q4_K_M` weights). Warm
runs reuse cached images and the model volume.

The test's bot-reply wait is best-effort and configurable. On a
CPU-only host inference takes minutes; on GPU it's <30s. Override the
default 5-minute reply window:

```bash
NETCLAW_RUN_DEMO_SMOKE=1 \
NETCLAW_DEMO_TEST_REPLY_TIMEOUT_SECONDS=900 \
  dotnet test samples/Netclaw.Demo.AppHost.IntegrationTests --filter Category=SlowSmoke
```

If the timeout elapses without a reply, the test still passes — the
structural assertions (every resource healthy, message posted into
Mattermost) prove the wiring. The latency is printed to stdout so a CI
run can flag a slow-inference regression.

## Install Script Smoke Test

`scripts/smoke/install-smoke.sh` and `scripts/smoke/install-smoke.ps1` are
hermetic regression tests for the installers (`scripts/install.sh` and
`scripts/install.ps1`). They need no network, no `dotnet` build, and no
running daemon — each serves a generated manifest and stand-in archives
from `localhost`.

| Command | Purpose |
|---------|---------|
| `bash scripts/smoke/install-smoke.sh` | Smoke-test the `curl \| bash` installer (Linux/macOS) |
| `pwsh scripts/smoke/install-smoke.ps1` | Smoke-test the PowerShell installer (Windows) |

`install-smoke.sh` covers two layers:

- **Detection matrix** — runs `install.sh --dry-run` under `uname`/`sysctl`
  shims to assert every supported OS/arch resolves to the right RID
  (`linux-x64`, `linux-arm64`, `osx-arm64`) and that Intel Macs and
  unsupported OSes are rejected cleanly. This runs identically on any host.
- **Mechanical check** — one real install of a stand-in archive on the
  host's native RID, exercising download → checksum → `tar` extract → `cp`.

`install-smoke.ps1` is the Windows counterpart: a `-DryRun` resolution
check plus a real stand-in install exercising download → checksum →
`Expand-Archive` → copy.

The `install-smoke` job in `pr_validation.yml` runs these on
`ubuntu-latest`, `macos-latest`, and `windows-latest` on every PR. Both
installers also support `--dry-run` / `-DryRun` on their own — they report
which binary *would* be installed for the current platform without
touching the system.

## Logging Conventions

- Carry cross-cutting identity (channel/adapter, session, entity ids) as
  **structured context via `ILoggingAdapter.WithContext(...)`**, set once where
  the logger is created — not baked into each message template. Actors already
  do this (e.g. `Context.GetLogger().WithContext("Adapter", "slack")`).
- Do **not** prefix the channel/adapter into the message text
  (`slack_attachment_rejected …`). Emit a plain semantic event
  (`attachment_rejected …`); the `Adapter` context disambiguates the source.
  Prefixing double-stamps what the context already carries and forces shared
  code to take a channel-name parameter purely for logging.
- Shared/abstracted helpers that take an `ILoggingAdapter` should rely on the
  caller's enriched context rather than re-passing identity strings.
- For `Microsoft.Extensions.Logging` (`ILogger<T>`) call sites, the logger
  **category** already identifies the type; prefer that (or `BeginScope`) over
  repeating the class/channel name in the message.

See `getakka.net` → Utilities → Logging → "Context enrichment and scopes".

## Source Control and CI Signals

- `git` repository with active `dev` branch
- GitHub Actions workflows in `.github/workflows/`
- Azure pipeline templates in `.azure/`

## External Integrations (Planned for MVP)

- Slack Socket Mode
  - requires bot token and app token
  - no public inbound HTTP required for base interaction
- SQLite for Akka.Persistence journal and snapshots (in-memory for tests)
- MCP servers for external tool integration (MVP requirement)
- local Ollama endpoint can be used for optional smoke tests
  - local dev host: `my-gpu-server` on Tailscale (`http://my-gpu-server:11434`)
  - preferred model: `qwen3:30b` (fallback `qwen3:14b`)

## Security-Relevant Surfaces

- Slack inbound message events (untrusted input)
- tool execution surfaces (web, file read/write, shell)
- ACL configuration and policy evaluation
- system prompt and policy files loaded from disk

## Operator Interfaces (Planned)

- CLI for onboarding/config validation, policy diagnostics, and session
  operations
- management UI (ops console) for health, session inspection, ACL editing, and
  diagnostics

## Working Assumptions

- single-process architecture during MVP
- operator-controlled host and credentials
- default-deny policy with explicit per-channel and per-sender allow rules
- required CI tests do not depend on live model providers
- `my-gpu-server` Ollama access is local-dev only and not available in CI/CD
