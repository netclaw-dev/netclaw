# Simplify Netclaw runtime decisions plan

Use the Poteto runtime contract at the installed `poteto-mode/references/codex-agent-runtime.md`.
Deliver .NET 11, closed authorization outcomes, pure delivery retries, and pure gateway reconnect decisions in PR1 through PR4.
Preserve operator behavior, public contracts, configuration, wire formats, journal records, snapshots, and authority.
Keep the current actor runtime. Add no Akka.Durable dependency or generic decider framework.
The user authorized execution after plan review and explicitly prohibited merges.
The user accepted focused verification instead of blanket ten-lane smoke runs.
Use required CI, focused mutations, compatibility checks, and affected native smoke. Add no standalone process probes.

## How to read this

One box is one unit of work. Every box names the evidence that checks it.
Check a box only when its evidence exists. Keep planned, local, hosted, and merged states separate.
The body is a how-to. The appendices explain the decisions and record the evidence.

Execution uses the installed `poteto-mode/playbooks/autopilot-stack.md` with the adaptations in Delivery.
The parent owns publication. The operator owns every merge. Stop after the verified PR handoff.

Verify each claim at its actual boundary. Reuse current-head evidence and preserve required repository gates.

## Delivery

Use these branches in order. The queue records each actual PR.

| Slice | Branch | Actual PR base | Required change |
|---|---|---|---|
| PR1 | `feature/net11-quality` | `dev` | Upgrade the runtime and compiler baseline. |
| PR2 | `feature/closed-authorization` | `feature/net11-quality` | Consolidate the internal authorization representation. |
| PR3 | `feature/pure-delivery-retry` | `feature/closed-authorization` | Move delivery retry decisions into one pure owner. |
| PR4 | `feature/pure-gateway-reconnect` | `feature/pure-delivery-retry` | Move reconnect decisions into one pure owner. |

PR2 through PR4 need the .NET 11 baseline for the selected closed types.
Their features otherwise remain separable. The linear stack is the user's chosen combined delivery sequence.
Do not invent a shared abstraction to create a dependency between the deciders.
Each slice must pass against its actual predecessor and remove its replaced internal mechanism in the same diff.
Consolidate tests within each slice. Do not create a separate test-only cleanup PR that hides a removed guarantee.

Use `github/gh-stack` version `0.1.0`, which is installed as the `gh stack` extension.
The upstream repository is `netclaw-dev/netclaw`. The local `origin` is a fork.
Pass `--remote upstream` on every submit, push, or sync operation that accepts that option.
Before init or submit, assert `gh repo view --json nameWithOwner` resolves `netclaw-dev/netclaw`.
Also assert `git remote get-url --push upstream` names that repository. API selection and push selection are separate checks.
Keep all stack branches upstream so dependent PRs can use them as bases.
Before init, compare `git rev-parse dev` with `git rev-parse upstream/dev`. The local trunk must match the chosen upstream base.
If they differ, move execution to a dedicated local clone with an `upstream` remote and import the unpublished plan branch.
Do not advance or reset the local `dev` branch that the dirty primary checkout owns.

After execution approval, register the existing root branch with the actual extension.
Run the following commands from the isolated stack worktree after each slice's local checks.

```bash
gh stack init --base dev feature/net11-quality
gh stack view --json
gh stack submit --auto --open --remote upstream
gh stack add feature/closed-authorization
# Implement and commit PR2 before the next submit.
gh stack submit --auto --open --remote upstream
gh stack add feature/pure-delivery-retry
# Implement and commit PR3 before the next submit.
gh stack submit --auto --open --remote upstream
gh stack add feature/pure-gateway-reconnect
# Implement and commit PR4 before the next submit.
gh stack submit --auto --open --remote upstream
```

The sequence above is a recipe. Do not execute all commands before each intervening slice passes its checks.
`submit --auto --open` creates PRs ready for review. Replace generated bodies with reviewed descriptions through `gh pr edit --body-file`.
Inspect existing labels and apply the appropriate repository labels.
`--open` also marks existing draft members ready. Inspect each member before a repeated submit.
If a member was deliberately returned to draft, omit `--open` and change readiness only for the intended new member.
Name the actual base PR in every dependent description. Verify all remote heads and bases after submission.
Publish each PR after local proof. Call it verified only after its exact head also passes required hosted checks.

The installed extension can use force-with-lease for existing remote branches during submit, push, and sync.
Before each such command, record every remote branch head and inspect its ancestry against the proposed local head.
Obtain explicit authority before an operation can force-push an existing shared branch. Initial branch publication needs no history rewrite.
`gh stack sync --remote upstream` also rebases branches and advances its local trunk.
Use a dedicated clone before sync can touch the primary checkout's `dev` branch.
A request to submit PRs does not authorize shared history rewrites.
Do not use sync merely to poll state. Use `gh stack view --json` and read-only GitHub checks instead.
Do not call `gh stack merge`. The operator owns the merge decision and the stack merge operation.
Do not replace the selected extension with Graphite or separate `gh pr create` calls.

## Program checklist

### Arm the program

- [x] Record execution authority and the no-merge boundary. Evidence is the user's proceed instruction.
- [x] Create a goal for the plan. Evidence is the task goal receipt.
- [x] Create the delivery goal and retain its receipt. The user authorized implementation and stack submission.
- [ ] Read repository guidance with `git show upstream/dev:AGENTS.md` and `git show upstream/dev:TOOLING.md`.
- [ ] Do not use the template command `git show origin/main:`. This repository uses `upstream/dev`.
- [ ] Read the installed execution, swarm, review, and relevant .NET skills by their actual paths.
- [ ] Record the queue table after each PR checkpoint. Skip an automatic hourly audit because the user requested no heartbeat.
- [ ] Resolve the API repository and push remote separately. Retain both preflight outputs before the first stack command.
- [ ] On a stop request, stop owned work and record the current head, artifacts, and remaining checks.

Use this future execution goal text.

> Deliver PR1 through PR4 from `.systematize/plans/net11-typed-decisions/plan.md` through `gh stack` against `netclaw-dev/netclaw`.
> Preserve the listed behavior and compatibility guarantees. Obtain independent verification and required hosted checks at each final head.
> Stop after the operator receives four verified PR links and exact head receipts. Do not merge or deploy.

### Spawn owners

- [ ] Give one owner exclusive write access to the active stack worktree for one slice at a time.
- [ ] Use `pstack-poteto-agent` for the owner. Use independent read-only reviewers for the combined diff and evidence.
- [ ] Validate each successor after its predecessor passes local checks. Private source preparation can overlap in a separate checkout.
- [ ] Keep only one writer in the stack checkout. Do not let reviews change files or share build outputs with another writer.
- [ ] Preserve the primary checkout and its untracked `PLUGIN_STACK_REBASE_PLAN.md`.
- [ ] Record the owner role, task scope, head SHA, fallback, and terminal report.

### PR mechanics, for every PR

- [ ] Refresh upstream state and inspect overlap before the first edit. Record the actual base SHA.
- [ ] Apply OpenSpec `simplify-runtime-decisions`. Use the canonical plan rather than a second task inventory.
- [ ] Use the smallest required change. Keep public adapters at actual compatibility boundaries.
- [ ] Run Slopwatch, copyright verification, and `git diff --check` after code changes.
- [ ] Apply Deslop before each commit. If absent, inspect dead paths, duplicate constructs, comments, and unexpected file changes directly.
- [ ] Run No Comments before review. Keep comments that explain a real security or lifecycle constraint.
- [ ] Submit through `gh stack submit --auto --open --remote upstream` and retain the remote stack output.
- [ ] Do not rename an upstream stack head. Use `feature/*` names from the start.
- [ ] Reconcile each PR body with its final scope. Preserve actual newlines with `--body-file`.
- [ ] Run long checks from an owned private launcher with a completion status file. Poll the owned process and artifacts.
- [ ] Treat a lost launcher session as no exit receipt. Preserve output bytes before cleanup when a fresh comparison needs them.

### Verdict and handoff, for every PR

- [ ] Obtain one gates report and two independent diff reviews with distinct behavior and compatibility focuses.
- [ ] Run the focused boundary checks for the active slice at its exact head.
- [ ] Reuse valid exact-head receipts from an already completed required smoke run. Do not rerun a scenario solely to fill another box.
- [ ] Record changed decision owners and actor fixture starts. Follow the Benchmark Checklist before any timing claim.
- [ ] Assess automated review comments from source and runtime evidence. Reject unsupported findings with a concrete reason.
- [ ] Verify the final hosted head, base, required checks, labels, and stack membership.
- [ ] Stop at verified handoff. Keep all merges and deployments outside the execution goal.

### Boot recipe, for every live lane

- [ ] Check out the exact head in the owned worktree and publish binaries once under the shared build lock from `TOOLING.md`.
- [ ] Use the repository native smoke driver `scripts/smoke/run-smoke.sh` with the installed VHS terminal control.
- [ ] Point `NETCLAW_SMOKE_CLI`, `NETCLAW_SMOKE_DAEMON`, and smoke-server variables at the head's published binaries.
- [ ] Give each lane a unique home, port, and `SMOKE_LOG_DIR` under the artifact root.
- [ ] Run native smoke lanes serially. The current harness uses `pkill -f` on the daemon binary path during cleanup.
- [ ] Keep other runs off that binary path until cleanup finishes. Distinct ports and homes alone do not isolate this cleanup.
- [ ] Capture only a sanitized result summary with VHS after each scenario finishes. Never display raw pairing or credential output in that frame.
- [ ] Keep raw scenario output private until secret removal completes. Revoke temporary credentials and remove their isolated home before artifact publication.
- [ ] Retain only semantic assertions, exit status, case IDs, and permitted diagnostics in the summary. Verify no generated credential survives.
- [ ] Add a disposable capture tape only if the existing harness lacks that frame.
- [ ] Retain exit status, semantic assertions, logs, and the exact head beside the PNG. A screenshot alone proves no backend guarantee.
- [ ] Use the named actor and contract fixtures for failure combinations that the public binary cannot inject.
- [ ] Reuse existing component and actor fixtures for failure injection. Add no standalone process driver.
- [ ] Report those fixture runs as component integration proof. Do not label them as real external transport execution.
- [ ] Remove every owned process and temporary home. Keep sanitized artifacts outside production data.
- [ ] If a native host, driver, or failure control is absent, record the missing evidence. Do not mark that lane passed.

## Upgrade to .NET 11 (PR1)

**Depends on.** None. Root base is `upstream/dev`.

**Files.**

- [x] Edit `global.json`, `Directory.Build.props`, and supported project target frameworks.
- [x] Review `Directory.Packages.props`, Aspire SDK references, workflow setup, and release scripts.
- [x] Update active runtime statements in `PROJECT_CONTEXT.md`, `TOOLING.md`, `CONTRIBUTING.md`, and the sample README.
- [x] Keep `Netclaw.Tools.Generators` on `netstandard2.0` unless compiler-host proof supports a change.
- [x] Include this plan and the OpenSpec metadata in the root PR.

**Build.**

- [x] Select the installed stable SDK `11.0.100` and an explicit supported language baseline. Verify resolver behavior without SDK 10.
- [x] Retarget product, test, smoke, sample, benchmark, tool, and coordination-observer projects where applicable.
- [x] Change package versions only when restore or compatibility evidence requires them. Use the package-management skill and CLI.
- [x] Preserve Akka 1.5 package versions unless a demonstrated incompatibility requires a separately reviewed scope change.
- [ ] Preserve Ubuntu Docker bases and the macOS ARM64 compression exception. Verify the self-contained binaries they receive.
- [x] Add no runtime policy changes, type conversions, or new permanent test fixtures in PR1.

**You see.**

- [ ] Existing commands and native smoke scenarios produce the same semantic results under .NET 11.
- [ ] A .NET 11 daemon restores a database and snapshots created by the baseline .NET 10 binary.

**Verify, unit.** Verify each claim at its actual boundary. Reuse current-head evidence and preserve required repository gates.

- [x] Run `dotnet tool restore`, `dotnet restore`, `dotnet build -c Release`, and `dotnet test -c Release` under the shared lock.
- [ ] Run the source generator tests and fixed-byte serializer and legacy replay fixtures. Record executed test counts.
- [ ] Run the native light smoke harness and the actual image startup check. Reuse exact-head lane receipts from that run without repetition.
- [x] Make `sessions-and-chat.sh` assert chat exit status and the deterministic reply before counting that lane as a pass.
- [x] Prove the smoke assertion rejects a failed chat. Keep this bounded harness repair in PR1 because the runtime gate depends on it.
- [ ] Create an isolated baseline database, record a turn and snapshot, stop the baseline daemon, and reopen that home with the candidate.
- [ ] Verify history, pending work, authority, and no duplicate completed turn. The exact upgrade-fixture recipe is enabling work before PR1 acceptance.
- [ ] Verify required CI hosts and release artifact construction. Report unrun native release targets as limits rather than release acceptance.
- [x] Keep the existing eval gate conditional. A TFM-only change does not require identity evals.

**Verify, live.**

- [ ] Run `./scripts/smoke/run-smoke.sh light` once against the published .NET 11 binaries.
- [ ] Reuse successful exact-head smoke receipts. Capture failures and semantic assertions without credential output.
- [ ] Verify the actual image startup and old database recovery with existing infrastructure.

**Verify, cost.**

- [ ] Add no permanent test fixtures for target-framework edits. Record supported-host results and unrun release-target limits.

**Review gate.** None. No interaction-specific operator review applies. Independent code reviews remain required. PR1 preserves the interaction contract and is not review-gated. An intended interaction change requires a scope revision.

**Handoff.**

- [ ] Obtain the parent verdict and required CI at the exact head. Retain the head and actual predecessor SHA.
- [ ] Verify the stack membership with `gh stack view --json`. Hand the PR to the operator and do not merge.

## Consolidate authorization outcomes (PR2)

**Depends on.** PR1. Base is `feature/net11-quality`.

**Files.**

- [ ] Edit `src/Netclaw.Actors/Authorization/AuthorizationDecision.cs` and the internal rule producers.
- [ ] Review `ToolAuthorizationDecision.cs`, `ToolAccessPolicy.cs`, `ToolAuthorizer.cs`, and `DispatchingToolExecutor.cs`.
- [ ] Update `docs/architecture/tool-authorization.md` when decision ownership changes.
- [ ] Extend existing authorization matrix and compatibility fixtures.
- [ ] Update affected anchors in `scripts/run-tool-authorization-mutations.sh` and `scripts/run-tool-authorizer-order-mutations.sh` with their documented expectations.

**Build.**

- [ ] Use one closed internal decision family with required payloads and exhaustive switch expressions.
- [ ] Migrate internal rule producers and consumers together. Remove duplicate outcome state and redundant conversion logic.
- [ ] Preserve `ToolAuthorizationDecision`, its public factories and properties, `IToolExecutor`, and public exception behavior.
- [ ] Retain the original public record and its generated semantics. Add an adapter only where a real public boundary requires one.
- [ ] Preserve public `Allowed` semantics until a separately authorized compatibility change. Internal code uses explicit decision cases.
- [ ] Preserve rule order, candidate coverage, exact shell analysis, launch reauthorization, and attended consent behavior.
- [ ] Preserve the valid pre-coverage consent candidate that carries temporary-path advice. Do not confuse it with a final consent outcome.
- [ ] Keep that intermediate typed and call-local. Only the terminal consent case excludes correction advice.
- [ ] Add one new case in an isolated compiler check and prove that omitted handling fails the build. Do not add a catch-all arm.

**You see.**

- [ ] The same call receives the same allow, deny, consent, or correction outcome and the same reviewed scope.
- [ ] An internal rule author updates one outcome model. Public callers retain their existing contract.

**Verify, unit.** Verify each claim at its actual boundary. Reuse current-head evidence and preserve required repository gates.

- [ ] Run existing `ShellApprovalDispositionMatrixTests` for Bash and PowerShell.
- [ ] Run `python3 scripts/authorization-corpus/run.py --base feature/net11-quality` after checking adapter compatibility.
- [ ] Require zero authorization differences, including differences that the outcome-direction check alone permits.
- [ ] Run `python3 scripts/check-approval-outcome-direction.py --base-ref feature/net11-quality`.
- [ ] Run `./scripts/run-tool-authorizer-order-mutations.sh` and `./scripts/run-tool-authorization-mutations.sh`.
- [ ] Preserve `ConsentWireCompatibilityTests`, serializer fixtures, and public consumer compile checks.
- [ ] Test covered and uncovered temporary-path candidates so advice stays in the current position relative to grant coverage.
- [ ] When the result shape moves an anchor, migrate the exact unsafe mutation and its kill expectation. Fail if it survives or does not run.
- [ ] Run the full required CI suite at the combined head. Update the mapped system skill only if agent-visible guidance changes.
**Verify, live.**

- [ ] Run existing authorization integration fixtures, the corpus differential, and focused mutation gates at the combined head.
- [ ] Run the native `approvals` tape for its UI contract. Do not claim that its revoke screen proves execution authorization.

**Verify, cost.**

- [ ] Record removed duplicate internal outcome state and retained public compatibility adapters.
- [ ] Make no speed claim without a controlled measurement. Add no new runtime benchmark gate.

**Review gate.** None. No interaction-specific operator review applies. Independent code reviews remain required. PR2 preserves the interaction contract and is not review-gated. An intended interaction change requires a scope revision.

**Handoff.**

- [ ] Obtain the parent verdict and required CI at the exact head. Retain the head and actual predecessor SHA.
- [ ] Verify the stack membership with `gh stack view --json`. Hand the PR to the operator and do not merge.

## Extract delivery retry decisions (PR3)

**Depends on.** PR1 is the language prerequisite. The chosen stack base is PR2 at `feature/closed-authorization`.

**Files.**

- [x] Edit `src/Netclaw.Actors/Sessions/Handlers/DeliveryRetryHandler.cs` and the delivery-feedback methods in `LlmSessionActor.cs`.
- [x] Extend `DeliveryRetryHandlerReminderTests` and the current session integration fixtures.
- [x] Remove only integration permutations whose replacement proves the same guarantee.

**Build.**

- [x] Replace split policy branches with a pure decision over the current retry facts, session phase, and feedback.
- [x] Return phase-specific actions from pure decisions. Keep the current state owner and all effects in the actor.
- [x] Preserve stale-turn rejection, the existing retry budget, routed-skill behavior, busy-phase behavior, and reminder attribution.
- [x] Keep the current event and snapshot fields. Do not journal actor-local decision steps.
- [x] Delete the old internal policy path at cutover. Keep no permanent shadow implementation.
- [x] Use named theory rows and generated sequences only where they prove distinct behavior. Reuse existing fixture helpers.

**You see.**

- [x] A current retryable failure requests the same retry. Stale feedback requests no retry.
- [x] Exhaustion reports the correct reminder once. A routed-skill turn never asks the main model to recreate its reply.

**Verify, unit.** Verify each claim at its actual boundary. Reuse current-head evidence and preserve required repository gates.

- [x] Run the direct fixture and the relevant session, replay, drain, and serialization cases. The focused actor run passed 138 tests.
- [x] Prove stale, exhausted, routed-skill, non-retryable, processing, compaction, and restart-drain outcomes through direct decisions.
- [x] Retain actor proof for the actual retry dispatch, attribution after replay and snapshot restore, and no extra model call.
- [x] Before deleting a heavy case, map its guarantee to the replacement and demonstrate the same unsafe mutation fails.
- [x] Review the focused mutation scope and reuse the reminder post-failure target documented in `TOOLING.md`.
- [ ] Run full required CI against PR2 plus PR3. Preserve all lifecycle and serialization assertions.
**Verify, live.**

- [x] Retain actor proof for model call counts, stale feedback, reminder attribution, and replay and snapshot restore.
- [x] Run `chat-admission-quit` and `reminder-run` native tapes against the candidate binary.

**Verify, cost.**

- [x] Record which equivalent policy permutations move to direct tests and which actor fixtures remain.
- [x] Require fewer actor starts for the migrated matrix without lost guarantees. Add no standalone process probe.

**Review gate.** None. No interaction-specific operator review applies. Independent code reviews remain required. PR3 preserves the interaction contract and is not review-gated. An intended interaction change requires a scope revision.

**Handoff.**

- [ ] Obtain the parent verdict and required CI at the exact head. Retain the head and actual predecessor SHA.
- [ ] Verify the stack membership with `gh stack view --json`. Hand the PR to the operator and do not merge.

## Extract gateway reconnect decisions (PR4)

**Depends on.** PR1 is the language prerequisite. The chosen stack base is PR3 at `feature/pure-delivery-retry`.

**Files.**

- [ ] Edit `src/Netclaw.Channels/ChannelLifecycleActor.cs` and add one closely related decision file only if needed.
- [ ] Combine the flap and stable-reset contract cases into one sequence per transport. Preserve all Discord timeout fixtures.
- [ ] Keep transport-specific registration and effects in the existing adapters.

**Build.**

- [ ] Reconcile open PR #2795 before the first edit. Preserve its stop deadline only if it enters the base.
- [ ] Extract pure retry-delay and stable-ready decisions. Keep phase and attempt identity in the current actor.
- [ ] Pass the current delay, ready timestamp, and current time as values. Keep transport effects and timers in the actor.
- [ ] Return the retry due time and next delay as one value. Add no lifecycle event or effect interpreter.
- [ ] Preserve readiness distinctions, stale completion rejection, stability-window reset, explicit disconnect, and subscription disposal.
- [ ] Delete the replaced decision branches. Do not duplicate the current transport base class with another framework.

**You see.**

- [ ] A connection that repeatedly drops grows the same delay. A stable connection resets the delay at the same boundary.
- [ ] Old completion messages change no current attempt. Explicit disconnect schedules no new reconnect.

**Verify, unit.** Verify each claim at its actual boundary. Reuse current-head evidence and preserve required repository gates.

- [ ] Run the existing `GatewayLifecycleContractTests` implementations and `DiscordGatewayLifecycleRetryTimeoutTests`.
- [ ] Add direct delay and stability cases. Retain actor cases for stale attempts, timeouts, readiness, and explicit disconnect.
- [ ] Retain component integration proof for real actor timer cancellation, clean transport stop, subscription disposal, and readiness callbacks.
- [ ] Retain each transport-specific assertion. Shared policy tests do not replace adapter conversion or effect proof.
- [ ] Review the focused mutation scope for stale-attempt rejection and reconnect-after-disconnect prevention.
- [ ] Run full required CI at the combined PR4 head and capture the final four-PR stack receipts.
**Verify, live.**

- [ ] Run gateway actor contract fixtures for real actor timers, stale completion, bounded stop, and subscription disposal.
- [ ] Run the native `config-channels` tape and daemon lifecycle scenario for affected binary and configuration contracts.

**Verify, cost.**

- [ ] Record fewer actor fixture starts for moved policy permutations. Preserve all transport-specific boundary assertions.
- [ ] Add no standalone process probe or runtime benchmark framework.

**Review gate.** None. No interaction-specific operator review applies. Independent code reviews remain required. PR4 preserves the interaction contract and is not review-gated. An intended interaction change requires a scope revision.

**Handoff.**

- [ ] Obtain the parent verdict and required CI at the exact head. Retain the head and actual predecessor SHA.
- [ ] Verify the stack membership with `gh stack view --json`. Hand the PR to the operator and do not merge.

## Close the program

- [ ] Complete all implementation and verification boxes with actual evidence before calling the delivery goal complete.
- [ ] Report every PR URL, head, base, local result, hosted result, unresolved risk, and unrun check.
- [ ] Confirm public behavior, public contracts, journal records, and configuration remain compatible.
- [ ] Keep the plan current while the stack remains active. Do not check off work that a future slice must complete.
- [ ] Archive OpenSpec only after its implementation and required verification finish through the appropriate skill.
- [ ] After later authorized merges, preserve the final plan in retained Git history before removing it from active context.


### Local runtime checkpoint

The Release build passes with zero errors and one ASPIRE010 warning.
The Linux non-NativeShell suite passes 16,235 tests and skips 99 expected platform or opt-in cases.
The final linked configuration theory passes three cases after its test-only revision.
The shared mutation invocation kills its eight existing mutants and the new link-check inversion.
The isolated inversion control fails all three link cases because the unsafe code accepts each dangling link.

Slopwatch reports zero issues. Copyright verification and the whitespace check pass.

Incrementalist prints its version and exits 232 under both SDK 10.0.403 and SDK 11.
This pre-existing tool result is not a successful execution claim. Required CI does not invoke that tool.
The published binaries identify commit `db9b20a4756ac557c1ab5273e2972e5885ed8b04`.
The actual image reaches healthy state and uses state owner `1654:1654`.
Both standalone projects build with zero warnings and errors.

All 29 native tapes and 10 scenarios pass their checks.
The launch shell exits 143 because the existing cleanup matches its command line.
The run home and owned services still disappear. A corrected file launcher then passes a one-tape cleanup check with exit zero.
The native scenario cannot prove model recall because the smoke model returns fixed text.
The database checks below prove history and turn continuity instead.

The released .NET 10 binary is version `0.28.3`, source `584023bda9ab4a30c7f3f6d8fbd0a7091b5a88ae`.
It creates a real completed turn, snapshots, and a title journal record.
The normal case restores its untouched logical database into the same home and loads the latest snapshot.
A second case selects its older snapshot to exercise the later title journal record.
Both candidates join at turn one, retain all four old serialized history messages, and complete turn two once.
Pending-work and authority recovery use the existing fixture suite rather than these two native cases.

The parent retains receipts under `/tmp/netclaw-quality-artifacts-20261117/pr1`.
The native log redacts generated pairing values. The harness deletes its temporary homes and credentials.
Independent reviews accept the source, mutation guard, and bounded upgrade claims.
Hosted checks and native release targets beyond Linux x64 remain unverified at this checkpoint.


### Hosted runtime follow-up

The initial PR1 head passes the Linux and macOS test jobs and every focused mutation group.
The Windows build finds nine additional character comparisons in six CLI files.
The fix adds explicit ordinal comparison and preserves the original character semantics.
A clean CLI build with `InvariantGlobalization=false` passes with zero warnings and errors.
Independent source review accepts the nine fixes. Slopwatch, headers, and the whitespace check pass.

The parent pushes commit `4bb384446d3d61f186e494e2c067c693d3b3ffa8` to PR1 by fast-forward.
The parent rebases the unpublished PR2 with `gh stack rebase --no-trunk --remote upstream`.
The primary checkout remains on its unchanged `dev` branch.
The macOS publish job in the first smoke run has no assigned runner or steps. It does not provide a code failure log.
The new source push starts fresh hosted checks. No job retry or merge is requested.

Wait for all hosted compile paths before another cold child corpus run after an SDK migration.
Private source preparation can continue in isolated checkouts while that proof runs.
The interrupted old-parent corpus results are not accepted as a completed comparison.
Use the documented CPU-based parallel lane count for the next full comparison; lane state and files remain isolated.

### Local authorization checkpoint

The internal producers now return the existing four decision cases. The terminal DTO conversion is removed.
The public record remains byte-identical to PR1. Its precompiled consumer produces identical output with the new assembly.
The actor and mutation projects compile with zero warnings and errors.
Focused actor tests pass 1,185 cases and skip three platform cases. Focused mutation-fixture tests pass 118 cases.
The paired structured-file theory proves that a covering grant wins before advice, while a miss produces correction.

The parent added a temporary fifth case as a compiler negative control.
Five actual production switches failed with `CS8509`. The parent restored the source bytes after the expected failure.
Slopwatch, copyright verification, and the whitespace check pass.
The corpus, focused Stryker gates, native authorization tapes, and hosted checks remain pending.

### Verified runtime and authorization checkpoints

PR1 head `4bb384446d3d61f186e494e2c067c693d3b3ffa8` passes all five required hosted workflows.
The hosted native smoke uses Linux and macOS binaries. The Linux image workflow passes.
A prior macOS test attempt failed one `ConfigWatcherActorTests` case. The next attempt passed.
The failure cause remains unproved. The unchanged watcher and test also exist on `dev`.
The old database and local image evidence use `db9b20a4756ac557c1ab5273e2972e5885ed8b04`.
PR1 adds only nine ordinal CLI comparisons after that head. The tested recovery and image boundaries stay unchanged.

PR2 tested source commit `5d5372a92c30df67e43edf8da1be0638c6dc770b` has source tree `3df7cfd614ff7914f9b7a768999bd68cdd653997`.
The full corpus compares it with PR1 head `4bb384446d3d61f186e494e2c067c693d3b3ffa8`.
Both sides contain 1,646,555 decisions. The official comparison reports zero differences and exits zero on repeat.
The outcome-direction check finds zero transitions across 639 rows.
The exact mutation gates kill three order, three authority, and two evidence mutants.
The reports also contain unrelated compile-error mutants outside the selected spans.

The focused actor run passes 1,185 cases and skips three platform cases. Mutation fixtures pass 118 cases.
The consent wire fixture covers journal text, event bytes, option keys, and v3 store bytes.
The separate legacy and serialization run passes 101 cases: 73 serialization, six deployed V2, 12 legacy coordination,
three SQLite, five child consent, and two abandonment cases.
A precompiled public consumer produces the same output. An added closed case breaks five incomplete production switches.
Linux x64 publication passes. The `approvals` and `mcp-permissions-save` tapes pass on the published binaries.
The CLI SHA-256 is `8c7f3aacc85b64ab33cc06a0a9222b89d284d3ab0bacf57f00c8a2784ec27f09`.
The daemon SHA-256 is `5b27e3ff972010d2ba965dbf7f9e5a8a913c77a5c15a16dc51758ed157c035c1`.
The temporary native home was removed. No candidate daemon remained after the harness exited zero.

The original corpus launcher session exited 143 while its runner and probe continued. The cause is unproved.
A lock guard protected the run. The team preserved the candidate output before the runner removed it.
The repeated official comparison obtained a separate exit-zero receipt.
The retained gzip passed its integrity check and reproduced the uncompressed head SHA-256.
The PR2 mutation selector check failed. The repair is pushed. Its hosted checks remain pending.

## PR3 local source receipt

The tested predecessor is `73bf713c04c28c6f8293b3de58d13902c8ab57a4`.
The focused actor suite passed 138 tests. The linked xUnit 2 fixture passed 45 tests.
The former Unknown actor case and its direct replacement both failed the same unsafe classification change.
After source restore, the direct and retained transport cases passed 46 tests.
The reminder gate killed 16 selected mutants. The combined gate killed 75 selected mutants.
The stale-turn inversion failed the earlier-turn direct case with `Stale` expected and `Retry` actual.
After restore, all four stale-turn cases passed. The actor suite retains retry, lifecycle, replay, snapshot, and serialization cases.
The direct matrix replaces one actor fixture. The two native tapes passed on candidate `3a2baecaa345c0a3d49dd83a4426cbbc593a4fd9`.
The native CLI and daemon report commit `3a2baec`. The CLI SHA-256 is `f7b22ef659e8c547ed0a545f31856058f7b3f7b0df99909cb8e35841da1d8b38`.
The daemon SHA-256 is `4535f07ce0ac6dfb866d6a4a7aa3b4a0ea040cb95323df2b750d01e060503564`.
The candidate source tree is `ed1740ac5cc2ad2bd041aff1e34fe3aefd058c98`. The scripts tree is `a2ff14828612312c1c1695aa9155912660aad160`.
The final receipt commit changes neither tree. The hosted checks remain pending.

### Authorization mutation selector repair

PR2 head `73bf713c04c28c6f8293b3de58d13902c8ab57a4` is open as [PR 2800](https://github.com/netclaw-dev/netclaw/pull/2800).
Hosted job `134354145796` failed when a no-program selector could not find the old result type name.
The literal-twin selector used the same old type name. Both selectors now name `AuthorizationDecision`.
The four selected source spans match the PR1 spans after the result type rename.
The no-program locator self-test and both static locator checks pass.
The required counts stay at 33 killed and two documented duplicate removals for no-program, and nine killed for literal twins.
The hosted mutation rerun must prove these counts at the corrected head. Local Stryker did not run for this repair.
The production source tree stays unchanged, so the earlier corpus and public compatibility evidence remains applicable.

## PR3 reminder test race receipt

Windows CI job `134382355152` recorded the third execution. A fixture read denied the next append handle.
The one-shot and denied-run polls now ask the manager for history. The manager serializes the query with settlement.
The manager and preflight first-create polls also use the query. CI did not show an append collision at those sites.
A gate holds the Nack lookup. The history query replies after release with the failed row and failure count.
This control proves actor ordering. It does not reproduce Windows file sharing on Linux.
The focused warm run passed 179 tests. The initial control caught the wrong storage call and failed; the corrected control passed.
The warm test build used existing dependency outputs and skipped restore. It started with 20,516,687,872 bytes free under the approved admission rule.
This test-only patch changes no reliability mutation target. The earlier delivery mutation receipts remain applicable.

## PR3 watcher test race receipt

The `analyze-racy-test` workflow used the Akka specialist first. The concurrency specialist then reviewed the result.
The watcher actor fixture mixed manual events with native callbacks. The macOS log does not identify the callback that caused its restart.
The actor now receives a required subscription factory. The host supplies the native watcher and the fixture supplies a controlled source.
The actor retains startup reconciliation, debounce, restart admission, and subscription disposal. The factory disposes a failed native allocation.
The controlled event tests cover ignored events and an accepted event followed by an ignored event. This control does not identify the historical callback.
The focused daemon run passed 41 tests: 25 actor, 13 configuration-source, and three real-host startup-gap cases.
The linked xUnit 2 configuration-source fixture passed 13 tests. The startup hash gate killed its one selected inversion.
Stryker reported 600 unrelated compiler artifacts outside the selected span. The focused gate found no unexpected in-scope mutant.
The focused build rebuilt product references with 20,359,421,952 bytes free under the approved focused-build rule.

## Current queue

| Slice | PR | Head | Actual base | Local state | Hosted state |
|---|---|---|---|---|---|
| PR1 | [2799](https://github.com/netclaw-dev/netclaw/pull/2799) | `4bb384446d3d61f186e494e2c067c693d3b3ffa8` | `dev` at `76071cfe9e721dee864d60d1379a497d97f19414` | Complete | All required workflows pass |
| PR2 | [2800](https://github.com/netclaw-dev/netclaw/pull/2800) | `9b2c84e5f18f562cd0c68840c2b2dae3f2228278` | PR1 at `4bb384446d3d61f186e494e2c067c693d3b3ffa8` | Corpus, mutations, and two native tapes pass on prior source-equivalent head | Repair pushed; checks pending |
| PR3 | Not submitted | Tested source candidate `3a2baecaa` | PR2 at `9b2c84e5f18f562cd0c68840c2b2dae3f2228278` | Local tests, controls, mutations, and both native tapes pass | Not submitted |
| PR4 | Not submitted | Not created | Planned PR3 | Private source preparation | Not submitted |

PR1 remains open and has no automatic merge request.
The parent prepared PR2 source in a separate checkout while the long native suite ran.
The parent used `gh stack add feature/closed-authorization` after PR1 local checks and submission.
The parent then applied the reviewed private patch to that canonical stack branch.
The canonical stack checkout holds the next slice. Separate checkouts isolate repairs to earlier PRs.
The parent restacks unpublished PR3 onto the selector repair. Its production source and retry gate files retain their tested bytes.

## Appendix A. Prototype evidence

The planning baseline is `76071cfe9e721dee864d60d1379a497d97f19414` on `upstream/dev`.
No product prototype, build, test, performance measurement, or live smoke ran during plan authorship.
The installed stable .NET 11 SDK is available. Its presence does not prove compatibility.

The parent ran the actual `gh stack` help for init, add, submit, view, sync, and merge.
The extension reports version `0.1.0`. Upstream reports default branch `dev` and account permission `ADMIN`.
No stack, remote branch, PR, merge, or deployment was created during the plan task.

The isolated planning worktree is `/home/petabridge/repositories/stannardlabs/netclaw-worktrees/net11-quality-20261117`.
The primary checkout remains on `dev` with its pre-existing untracked plan file untouched.

The .NET 10 database upgrade and native release-target execution remain unproven until their actual checks run.
Use existing actor fixtures for policy failure injection. The focused-verification override removes the proposed standalone process controls.
Prepare and run those procedures before the relevant PR can receive a verified verdict.

## Appendix B. Alternatives rejected

Keep the current actor runtime. Akka.Durable migration does not yet identify enough existing machinery that it can remove.
Do not add journal records merely to make a policy testable.
Do not convert every enum or interface into a union. Use closed cases where outcomes require different data.
Do not delete public authorization APIs or executor exception contracts as an internal cleanup.
Do not add a generic internal decider framework. Each local function must remove an existing split in policy or state.
Do not use a test-count or line-count reduction as the acceptance target.

Laziness Protocol keeps this work local and excludes a new runtime framework.
Model the Domain uses phase-specific delivery outcomes and an explicit gateway retry schedule.
Sequence Work into Verifiable Units makes the runtime upgrade the first independently verified slice.
Test Behavior, Not Implementation retains real persistence, authority, and lifecycle proof after policy test migration.


The authorization design retains the original public record and reuses the existing internal terminal hierarchy.
A wrapper around the public record would require custom code to preserve generated record semantics.

The delivery design keeps the current mutable handler and returns phase-specific actions from pure functions.
A full reducer would change snapshot adapters and mutation selectors without a behavior requirement.

The gateway design extracts delay and stability rules. The actor retains phase, attempt identity, and transport effects.
A full lifecycle reducer would require broad adapter changes and another mechanism to coordinate the active Akka behavior.
Combine the flap and stable-reset actor tests into one sequence per transport. Preserve both actual timer guarantees.

## Appendix C. Risks

PR1 can expose SDK, analyzer, native library, source generator, or Aspire incompatibilities.
Report exact restore and runtime failures before adding package upgrades.
Keep the existing macOS ARM64 compression exception unless native execution disproves its need.
Missing native ARM64 or Windows execution evidence remains a release risk, not a passed check.

PR2 touches public facade types and security decisions.
Preserve the public facade and exception seam. Stop if the proposed simplification requires a public break or changed grant scope.
The authorization corpus must compare against the actual predecessor and report zero differences.

PR3 must preserve the existing durable retry attribution and actor-local counters at their current lifetimes.
A pure decision must not become a second authoritative copy of session state.

PR4 overlaps open PR #2795, which changes the gateway stop timeout.
Check the live head and merge state before the slice starts. Preserve any incorporated stop deadline.
Other open changes cover child consent, session tool execution, and shell launch. Reconcile their actual diffs before edits.

The user superseded the Poteto ten-lane template with focused verification.
The original plan checker enforces that superseded template and does not validate the revised execution plan.
Use strict OpenSpec validation, local link checks, independent review, and the actual repository gates.
Reuse valid exact-head receipts. Native smoke runs remain serial because cleanup matches the daemon path.
Queue reviews within actual agent capacity.
Keep ports, process ownership, homes, and build outputs isolated.
If the control driver or evidence capture cannot exercise a boundary, report the gap without replacing it with a screenshot.

No runtime reduction or performance result exists yet. Report structural test-cost changes separately from measured speed.
Read the Benchmark Checklist before any performance measurement. No blanket benchmark gate applies to these refactors.

## Appendix D. Links and reading list

Read [the engineering glossary](../../../docs/spec/GLOSSARY.md), [AGENTS.md](../../../AGENTS.md), and [TOOLING.md](../../../TOOLING.md).
Read [the source proposal](../../../openspec/changes/simplify-runtime-decisions/proposal.md) and the linked preserved capabilities.
Use [tool authorization](../../../docs/architecture/tool-authorization.md) for decision ownership and launch constraints.
Use [session requirements](../../../openspec/specs/netclaw-session/spec.md), [test requirements](../../../openspec/specs/netclaw-testing/spec.md), and channel specifications.
Read PRD-001 FR-002, FR-003, FR-005, FR-008, and FR-011. Preserve PRD-002 SEC-001, SEC-003, SEC-006, SEC-007, and SEC-009.

Use OpenSpec Apply for implementation and OpenSpec Verify and Archive at their actual lifecycle boundaries.
Use API Design for the released facade, Modern C# Standards for implementation, and Package Management for dependency changes.
Use the installed Architect skill before a material implementation shape crosses function boundaries.
Use Interrogate only if a consequential design remains contested before publication.
Use Slopwatch after code edits and focused mutation gates for the changed authority and reliability boundaries.
Use the native smoke harness for the actual published binaries. Run identity evals only when mapped prompt or skill guidance changes.

Keep the decision trail at `.systematize/plans/net11-typed-decisions/decisions.tsv` through Show Me Your Work.
Do not copy this checklist into another plan, issue, or agent memory. Link this file from PR descriptions.
