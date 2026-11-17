# Simplify Netclaw runtime decisions plan

Use the Poteto runtime contract at the installed `poteto-mode/references/codex-agent-runtime.md`.
Deliver .NET 11, closed authorization outcomes, pure delivery retries, and pure gateway reconnect decisions in PR1 through PR4.
Preserve operator behavior, public contracts, configuration, wire formats, journal records, snapshots, and authority.
Keep the current actor runtime. Add no Akka.Durable dependency or generic decider framework.
The user requested this plan and selected a stop for review before implementation.
The active Poteto template requires 40 live assignments across the four PRs. Native runs serialize and reuse valid exact-head smoke receipts.
Their runtime is unmeasured. Three affected-path controls remain enabling work. Review this cost before execution.

## How to read this

One box is one unit of work. Every box names the evidence that checks it.
Check a box only when its evidence exists. Keep planned, local, hosted, and merged states separate.
The body is a how-to. The appendices explain the decisions and record the evidence.

Execution uses the installed `poteto-mode/playbooks/autopilot-stack.md` with the adaptations in Delivery.
The parent owns publication. The operator owns every merge. Stop after the verified PR handoff.

Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

## Delivery

Use these branches in order. PR numbers do not exist yet.

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

- [x] Record the user's stop for review. Evidence is the scope answer in this task.
- [x] Create a goal for the plan. Evidence is the task goal receipt.
- [ ] After explicit execution approval, create a goal with the objective below and retain its receipt.
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
- [ ] Start PR2 after PR1 passes local checks. Start PR3 after PR2 passes. Start PR4 after PR3 passes.
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

### Verdict and merge, for every PR

- [ ] Obtain one gates report and two independent diff reviews with distinct behavior and compatibility focuses.
- [ ] Complete the ten live verification assignments below at the exact head. Queue work within the available agent capacity.
- [ ] Reuse valid exact-head receipts from an already completed required smoke run. Do not rerun a scenario solely to fill another box.
- [ ] Run the bounded cost comparison against the actual predecessor. Follow the Benchmark Checklist before measurement.
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
- [ ] Prepare the affected-path probes named by PR2 through PR4 before their live assignments run. Keep injection controls in test infrastructure only.
- [ ] Report those fixture runs as component integration proof. Do not label them as real external transport execution.
- [ ] Remove every owned process and temporary home. Keep sanitized artifacts outside production data.
- [ ] If a native host, driver, or failure control is absent, record the missing evidence. Do not mark that lane passed.

## Upgrade to .NET 11 (PR1)

**Depends on.** None. Root base is `upstream/dev`.

**Files.**

- [ ] Edit `global.json`, `Directory.Build.props`, and supported project target frameworks.
- [ ] Review `Directory.Packages.props`, Aspire SDK references, workflow setup, and release scripts.
- [ ] Update active runtime statements in `PROJECT_CONTEXT.md`, `TOOLING.md`, `CONTRIBUTING.md`, and the sample README.
- [ ] Keep `Netclaw.Tools.Generators` on `netstandard2.0` unless compiler-host proof supports a change.
- [ ] Include this plan and the OpenSpec metadata in the root PR.

**Build.**

- [ ] Select the installed stable SDK `11.0.100` and an explicit supported language baseline. Verify resolver behavior without SDK 10.
- [ ] Retarget product, test, smoke, sample, benchmark, tool, and coordination-observer projects where applicable.
- [ ] Change package versions only when restore or compatibility evidence requires them. Use the package-management skill and CLI.
- [ ] Preserve Akka 1.5 package versions unless a demonstrated incompatibility requires a separately reviewed scope change.
- [ ] Preserve Ubuntu Docker bases and the macOS ARM64 compression exception. Verify the self-contained binaries they receive.
- [ ] Add no runtime policy changes, type conversions, or new permanent test fixtures in PR1.

**You see.**

- [ ] Existing commands and native smoke scenarios produce the same semantic results under .NET 11.
- [ ] A .NET 11 daemon restores a database and snapshots created by the baseline .NET 10 binary.

**Verify, unit.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Run `dotnet tool restore`, `dotnet restore`, `dotnet build -c Release`, and `dotnet test -c Release` under the shared lock.
- [ ] Run the source generator tests and fixed-byte serializer and legacy replay fixtures. Record executed test counts.
- [ ] Run the native light smoke harness and the actual image startup check. Reuse exact-head lane receipts from that run without repetition.
- [ ] Make `sessions-and-chat.sh` assert chat exit status and the deterministic reply before counting that lane as a pass.
- [ ] Prove the smoke assertion rejects a failed chat. Keep this bounded harness repair in PR1 because the runtime gate depends on it.
- [ ] Create an isolated baseline database, record a turn and snapshot, stop the baseline daemon, and reopen that home with the candidate.
- [ ] Verify history, pending work, authority, and no duplicate completed turn. The exact upgrade-fixture recipe is enabling work before PR1 acceptance.
- [ ] Publish Linux x64, Linux ARM64, Windows x64, and macOS ARM64 artifacts. Require native execution evidence for each supported runtime.
- [ ] Keep the existing eval gate conditional. A TFM-only change does not require identity evals.

**Verify, live.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked. Ten lanes on the configured fast profile at the PR head, per the boot recipe.

- [ ] Lane 1. Run `./scripts/smoke/run-smoke.sh help`. Save `PR1/lane-01.png`. Pass when the published CLI help command succeeds.
- [ ] Lane 2. Run `./scripts/smoke/run-smoke.sh doctor`. Save `PR1/lane-02.png`. Pass when the doctor scenario produces its expected exit status and diagnostics.
- [ ] Lane 3. Run `./scripts/smoke/run-smoke.sh daemon-lifecycle`. Save `PR1/lane-03.png`. Pass when the daemon starts, answers readiness, and stops under the correct home.
- [ ] Lane 4. Run `./scripts/smoke/run-smoke.sh sessions-and-chat`. Save `PR1/lane-04.png`. Pass when the scripted chat completes with the expected session output.
- [ ] Lane 5. Run `./scripts/smoke/run-smoke.sh approvals`. Save `PR1/lane-05.png`. Pass when the native approval tape preserves the expected consent controls.
- [ ] Lane 6. Run `./scripts/smoke/run-smoke.sh pairing`. Save `PR1/lane-06.png`. Pass when the pairing scenario preserves accepted and rejected credential cases.
- [ ] Lane 7. Run `./scripts/smoke/run-smoke.sh reminders`. Save `PR1/lane-07.png`. Pass when the reminder scenario reports the expected terminal result.
- [ ] Lane 8. Run `./scripts/smoke/run-smoke.sh mcp-setup`. Save `PR1/lane-08.png`. Pass when the MCP scenario preserves discovery and permission behavior.
- [ ] Lane 9. Run `./scripts/smoke/run-smoke.sh init-wizard`. Save `PR1/lane-09.png`. Pass when the native wizard saves the canonical configuration.
- [ ] Lane 10. Run `./scripts/smoke/run-smoke.sh chat-admission-quit`. Save `PR1/lane-10.png`. Pass when the chat tape admits input and quits without a duplicate turn.

**Verify, perf.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Restore and test duration, published CLI startup duration, and the existing journal trace.
- [ ] Probe. Capture five alternating baseline and head samples on the same host and configuration. Reuse existing native startup and test commands.
- [ ] Baseline. Record the refreshed upstream base SHA used by PR1 before measurement. Keep `76071cfe9` only as the planning snapshot. Preserve SDK and RID metadata.
- [ ] Rule. Reject an unexplained median regression above 15 percent. Treat overlapping noisy samples as inconclusive. Require unchanged journal semantics.

**Review gate.** None. No interaction-specific operator review applies. Independent code reviews remain required. PR1 preserves the interaction contract and is not review-gated. An intended interaction change requires a scope revision.

**Merge.**

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
- [ ] Keep one adapter at each released boundary. Derive facade properties from the canonical result instead of storing parallel facts.
- [ ] Preserve public `Allowed` semantics until a separately authorized compatibility change. Internal code uses explicit decision cases.
- [ ] Preserve rule order, candidate coverage, exact shell analysis, launch reauthorization, and attended consent behavior.
- [ ] Preserve the valid pre-coverage consent candidate that carries temporary-path advice. Do not confuse it with a final consent outcome.
- [ ] Keep that intermediate typed and call-local. Only the terminal consent case excludes correction advice.
- [ ] Add one new case in an isolated compiler check and prove that omitted handling fails the build. Do not add a catch-all arm.

**You see.**

- [ ] The same call receives the same allow, deny, consent, or correction outcome and the same reviewed scope.
- [ ] An internal rule author updates one outcome model. Public callers retain their existing contract.

**Verify, unit.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Run existing `ShellApprovalDispositionMatrixTests` for Bash and PowerShell.
- [ ] Run `python3 scripts/authorization-corpus/run.py --base feature/net11-quality` after checking adapter compatibility.
- [ ] Require zero authorization differences, including differences that the outcome-direction check alone permits.
- [ ] Run `python3 scripts/check-approval-outcome-direction.py --base-ref feature/net11-quality`.
- [ ] Run `./scripts/run-tool-authorizer-order-mutations.sh` and `./scripts/run-tool-authorization-mutations.sh`.
- [ ] Preserve `ConsentWireCompatibilityTests`, serializer fixtures, and public consumer compile checks.
- [ ] Test covered and uncovered temporary-path candidates so advice stays in the current position relative to grant coverage.
- [ ] When the result shape moves an anchor, migrate the exact unsafe mutation and its kill expectation. Fail if it survives or does not run.
- [ ] Run the full required CI suite at the combined head. Update the mapped system skill only if agent-visible guidance changes.
- [ ] Prepare an authorization live probe through the existing smoke provider and native daemon harness.
- [ ] Script an allowed session file read and a protected-file denial. Assert tool results and no denied side effect.
- [ ] Add only test-side controls for those calls. The current smoke provider emits a fixed reply, so this control is an enabling task.

**Verify, live.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked. Ten lanes on the configured fast profile at the PR head, per the boot recipe.

- [ ] Lane 1. Run `./scripts/smoke/run-smoke.sh help`. Save `PR2/lane-01.png`. Pass when the published CLI help command succeeds.
- [ ] Lane 2. Run `./scripts/smoke/run-smoke.sh doctor`. Save `PR2/lane-02.png`. Pass when the doctor scenario produces its expected exit status and diagnostics.
- [ ] Lane 3. Run `./scripts/smoke/run-smoke.sh daemon-lifecycle`. Save `PR2/lane-03.png`. Pass when the daemon starts, answers readiness, and stops under the correct home.
- [ ] Lane 4. Run `./scripts/smoke/run-smoke.sh sessions-and-chat`. Save `PR2/lane-04.png`. Pass when the scripted chat completes with the expected session output.
- [ ] Lane 5. Run the proposed authorization live probe after its enabling task. Save `PR2/lane-05.png`. Pass when the allowed read succeeds and the protected read denies without a side effect.
- [ ] Lane 6. Run `./scripts/smoke/run-smoke.sh pairing`. Save `PR2/lane-06.png`. Pass when the pairing scenario preserves accepted and rejected credential cases.
- [ ] Lane 7. Run `./scripts/smoke/run-smoke.sh reminders`. Save `PR2/lane-07.png`. Pass when the reminder scenario reports the expected terminal result.
- [ ] Lane 8. Run `./scripts/smoke/run-smoke.sh mcp-setup`. Save `PR2/lane-08.png`. Pass when the MCP scenario preserves discovery and permission behavior.
- [ ] Lane 9. Run `./scripts/smoke/run-smoke.sh init-wizard`. Save `PR2/lane-09.png`. Pass when the native wizard saves the canonical configuration.
- [ ] Lane 10. Run `./scripts/smoke/run-smoke.sh chat-admission-quit`. Save `PR2/lane-10.png`. Pass when the chat tape admits input and quits without a duplicate turn.

**Verify, perf.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Authorization corpus outcomes, policy evaluation cost, public contract differences, and journal trace.
- [ ] Probe. Compare the same frozen corpus and fixed workload on PR1 and PR2. Collect five alternating samples after one warmup.
- [ ] Baseline. Record the exact PR1 head and its corpus, runtime, and public contract results.
- [ ] Rule. Require zero changed corpus outcomes and zero unapproved public or durable contract changes. Investigate a median cost increase above 15 percent.

**Review gate.** None. No interaction-specific operator review applies. Independent code reviews remain required. PR2 preserves the interaction contract and is not review-gated. An intended interaction change requires a scope revision.

**Merge.**

- [ ] Obtain the parent verdict and required CI at the exact head. Retain the head and actual predecessor SHA.
- [ ] Verify the stack membership with `gh stack view --json`. Hand the PR to the operator and do not merge.

## Extract delivery retry decisions (PR3)

**Depends on.** PR1 is the language prerequisite. The chosen stack base is PR2 at `feature/closed-authorization`.

**Files.**

- [ ] Edit `src/Netclaw.Actors/Sessions/Handlers/DeliveryRetryHandler.cs` and the delivery-feedback methods in `LlmSessionActor.cs`.
- [ ] Extend `DeliveryRetryHandlerReminderTests` and the current session integration fixtures.
- [ ] Remove only integration permutations whose replacement proves the same guarantee.

**Build.**

- [ ] Replace split policy branches with a pure decision over the current retry facts, session phase, and feedback.
- [ ] Return the next state and closed requested actions. Keep model calls, logs, notices, and persistence in the actor.
- [ ] Preserve stale-turn rejection, the existing retry budget, routed-skill behavior, busy-phase behavior, and reminder attribution.
- [ ] Keep the current event and snapshot fields. Do not journal actor-local decision steps.
- [ ] Delete the old internal policy path at cutover. Keep no permanent shadow implementation.
- [ ] Use named theory rows and generated sequences only where they prove distinct behavior. Reuse existing fixture helpers.

**You see.**

- [ ] A current retryable failure requests the same retry. Stale feedback requests no retry.
- [ ] Exhaustion reports the correct reminder once. A routed-skill turn never asks the main model to recreate its reply.

**Verify, unit.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Run `dotnet test src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj -c Release --filter "FullyQualifiedName~DeliveryRetryHandlerReminderTests|FullyQualifiedName~LlmSessionIntegrationTests"`.
- [ ] Prove stale, exhausted, routed-skill, non-retryable, processing, compaction, and restart-drain outcomes through direct decisions.
- [ ] Retain actor proof for the actual retry dispatch, attribution after replay and snapshot restore, and no extra model call.
- [ ] Before deleting a heavy case, map its guarantee to the replacement and demonstrate the same unsafe mutation fails.
- [ ] Review the focused mutation scope and reuse the reminder post-failure target documented in `TOOLING.md`.
- [ ] Run full required CI against PR2 plus PR3. Preserve all lifecycle and serialization assertions.
- [ ] Prepare a test-only process probe that drives the production session with correlated delivery feedback.
- [ ] Reuse the existing session fixture setup. Observe model request counts for current, stale, and exhausted feedback.
- [ ] Keep feedback injection outside shipped endpoints. Until this control exists, the affected-path live assignment remains blocked.

**Verify, live.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked. Ten lanes on the configured fast profile at the PR head, per the boot recipe.

- [ ] Lane 1. Run `./scripts/smoke/run-smoke.sh help`. Save `PR3/lane-01.png`. Pass when the published CLI help command succeeds.
- [ ] Lane 2. Run `./scripts/smoke/run-smoke.sh doctor`. Save `PR3/lane-02.png`. Pass when the doctor scenario produces its expected exit status and diagnostics.
- [ ] Lane 3. Run `./scripts/smoke/run-smoke.sh daemon-lifecycle`. Save `PR3/lane-03.png`. Pass when the daemon starts, answers readiness, and stops under the correct home.
- [ ] Lane 4. Run the proposed delivery feedback process probe after its enabling task. Save `PR3/lane-04.png`. Pass when current feedback retries within budget and stale or exhausted feedback starts no model call.
- [ ] Lane 5. Run `./scripts/smoke/run-smoke.sh approvals`. Save `PR3/lane-05.png`. Pass when the native approval tape preserves the expected consent controls.
- [ ] Lane 6. Run `./scripts/smoke/run-smoke.sh pairing`. Save `PR3/lane-06.png`. Pass when the pairing scenario preserves accepted and rejected credential cases.
- [ ] Lane 7. Run `./scripts/smoke/run-smoke.sh reminders`. Save `PR3/lane-07.png`. Pass when the reminder scenario reports the expected terminal result.
- [ ] Lane 8. Run `./scripts/smoke/run-smoke.sh mcp-setup`. Save `PR3/lane-08.png`. Pass when the MCP scenario preserves discovery and permission behavior.
- [ ] Lane 9. Run `./scripts/smoke/run-smoke.sh init-wizard`. Save `PR3/lane-09.png`. Pass when the native wizard saves the canonical configuration.
- [ ] Lane 10. Run `./scripts/smoke/run-smoke.sh chat-admission-quit`. Save `PR3/lane-10.png`. Pass when the chat tape admits input and quits without a duplicate turn.

**Verify, perf.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Actor fixture starts for the migrated matrix, focused suite duration, model call count, and journal trace.
- [ ] Probe. Run the identical named behavior matrix against PR2 and PR3. Record five alternating samples and actual fixture starts.
- [ ] Baseline. Record the exact PR2 head, matrix case IDs, expensive fixture count, and trace.
- [ ] Rule. Require fewer actor fixture starts for the migrated matrix, unchanged behavior and journal trace, and no unexplained median regression above 15 percent.

**Review gate.** None. No interaction-specific operator review applies. Independent code reviews remain required. PR3 preserves the interaction contract and is not review-gated. An intended interaction change requires a scope revision.

**Merge.**

- [ ] Obtain the parent verdict and required CI at the exact head. Retain the head and actual predecessor SHA.
- [ ] Verify the stack membership with `gh stack view --json`. Hand the PR to the operator and do not merge.

## Extract gateway reconnect decisions (PR4)

**Depends on.** PR1 is the language prerequisite. The chosen stack base is PR3 at `feature/pure-delivery-retry`.

**Files.**

- [ ] Edit `src/Netclaw.Channels/ChannelLifecycleActor.cs` and add one closely related decision file only if needed.
- [ ] Extend existing gateway lifecycle contract and Discord timeout fixtures.
- [ ] Keep transport-specific registration and effects in the existing adapters.

**Build.**

- [ ] Reconcile open PR #2795 before the first edit. Preserve its bounded transport-stop guarantee if it enters the base.
- [ ] Model connection phase, attempt identity, retry delay, and stable-ready time as one decision state.
- [ ] Pass the current time and normalized transport events as values. Keep transport instances and timer handles outside the decision.
- [ ] Return required start, stop, retry, reply, and notification actions as closed data.
- [ ] Preserve readiness distinctions, stale completion rejection, stability-window reset, explicit disconnect, and subscription disposal.
- [ ] Delete the replaced decision branches. Do not duplicate the current transport base class with another framework.

**You see.**

- [ ] A connection that repeatedly drops grows the same delay. A stable connection resets the delay at the same boundary.
- [ ] Old completion messages change no current attempt. Explicit disconnect schedules no new reconnect.

**Verify, unit.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Run the existing `GatewayLifecycleContractTests` implementations and `DiscordGatewayLifecycleRetryTimeoutTests`.
- [ ] Add direct transition cases for stale start, stale timeout, transient failure, readiness, flapping, stable reset, and explicit disconnect.
- [ ] Retain component integration proof for real actor timer cancellation, clean transport stop, subscription disposal, and readiness callbacks.
- [ ] Retain each transport-specific assertion. Shared policy tests do not replace adapter conversion or effect proof.
- [ ] Review the focused mutation scope for stale-attempt rejection and reconnect-after-disconnect prevention.
- [ ] Run full required CI at the combined PR4 head and capture the final four-PR stack receipts.
- [ ] Prepare a test-only process probe from the current gateway contract setup with a deterministic transport.
- [ ] Drive connect, drop, retry, stale completion, and explicit disconnect through the production actor. Observe its transport effect trace.
- [ ] Keep this probe separate from external platform claims. Until its control exists, the affected-path live assignment remains blocked.

**Verify, live.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked. Ten lanes on the configured fast profile at the PR head, per the boot recipe.

- [ ] Lane 1. Run `./scripts/smoke/run-smoke.sh help`. Save `PR4/lane-01.png`. Pass when the published CLI help command succeeds.
- [ ] Lane 2. Run `./scripts/smoke/run-smoke.sh doctor`. Save `PR4/lane-02.png`. Pass when the doctor scenario produces its expected exit status and diagnostics.
- [ ] Lane 3. Run the proposed gateway process probe after its enabling task. Save `PR4/lane-03.png`. Pass when the effect trace preserves backoff, rejects stale completions, and schedules no reconnect after disconnect.
- [ ] Lane 4. Run `./scripts/smoke/run-smoke.sh sessions-and-chat`. Save `PR4/lane-04.png`. Pass when the scripted chat completes with the expected session output.
- [ ] Lane 5. Run `./scripts/smoke/run-smoke.sh approvals`. Save `PR4/lane-05.png`. Pass when the native approval tape preserves the expected consent controls.
- [ ] Lane 6. Run `./scripts/smoke/run-smoke.sh pairing`. Save `PR4/lane-06.png`. Pass when the pairing scenario preserves accepted and rejected credential cases.
- [ ] Lane 7. Run `./scripts/smoke/run-smoke.sh reminders`. Save `PR4/lane-07.png`. Pass when the reminder scenario reports the expected terminal result.
- [ ] Lane 8. Run `./scripts/smoke/run-smoke.sh mcp-setup`. Save `PR4/lane-08.png`. Pass when the MCP scenario preserves discovery and permission behavior.
- [ ] Lane 9. Run `./scripts/smoke/run-smoke.sh init-wizard`. Save `PR4/lane-09.png`. Pass when the native wizard saves the canonical configuration.
- [ ] Lane 10. Run `./scripts/smoke/run-smoke.sh chat-admission-quit`. Save `PR4/lane-10.png`. Pass when the chat tape admits input and quits without a duplicate turn.

**Verify, perf.** Tests alone are not sufficient verification. A PR is verified only when its unit, live, and perf boxes are all checked.

- [ ] Metric. Actor fixture starts for shared policy permutations, reconnect action count, focused suite duration, and journal trace.
- [ ] Probe. Compare the same event sequences and contract fixtures on PR3 and PR4 with five alternating samples.
- [ ] Baseline. Record the exact PR3 head, case IDs, fixture starts, and reconnect trace.
- [ ] Rule. Require fewer expensive fixture starts for moved permutations, unchanged transport effects, zero new journal records, and no unexplained median regression above 15 percent.

**Review gate.** None. No interaction-specific operator review applies. Independent code reviews remain required. PR4 preserves the interaction contract and is not review-gated. An intended interaction change requires a scope revision.

**Merge.**

- [ ] Obtain the parent verdict and required CI at the exact head. Retain the head and actual predecessor SHA.
- [ ] Verify the stack membership with `gh stack view --json`. Hand the PR to the operator and do not merge.

## Close the program

- [ ] Complete all implementation and verification boxes with actual evidence before calling the delivery goal complete.
- [ ] Report every PR URL, head, base, local result, hosted result, unresolved risk, and unrun check.
- [ ] Confirm public behavior, public contracts, journal records, and configuration remain compatible.
- [ ] Keep the plan current while the stack remains active. Do not check off work that a future slice must complete.
- [ ] Archive OpenSpec only after its implementation and required verification finish through the appropriate skill.
- [ ] After later authorized merges, preserve the final plan in retained Git history before removing it from active context.

## Appendix A. Prototype evidence

The planning baseline is `76071cfe9e721dee864d60d1379a497d97f19414` on `upstream/dev`.
No product prototype, build, test, performance measurement, or live smoke ran during plan authorship.
The installed stable .NET 11 SDK is available. Its presence does not prove compatibility.

The parent ran the actual `gh stack` help for init, add, submit, view, sync, and merge.
The extension reports version `0.1.0`. Upstream reports default branch `dev` and account permission `ADMIN`.
No stack, remote branch, PR, merge, or deployment was created during the plan task.

The isolated planning worktree is `/home/petabridge/repositories/stannardlabs/netclaw-worktrees/net11-quality-20261117`.
The primary checkout remains on `dev` with its pre-existing untracked plan file untouched.

The exact .NET 10 database upgrade procedure, native RID execution, and sanitized terminal-frame recipe remain unproven.
The authorization, delivery-feedback, and gateway process controls also remain unbuilt. Their dependent acceptance boxes stay unchecked.
Prepare and run those procedures before the relevant PR can receive a verified verdict.

## Appendix B. Alternatives rejected

Keep the current actor runtime. Akka.Durable migration does not yet identify enough existing machinery that it can remove.
Do not add journal records merely to make a policy testable.
Do not convert every enum or interface into a union. Use closed cases where outcomes require different data.
Do not delete public authorization APIs or executor exception contracts as an internal cleanup.
Do not add a generic internal decider framework. Each local function must remove an existing split in policy or state.
Do not use a test-count or line-count reduction as the acceptance target.

Laziness Protocol keeps this work local and excludes a new runtime framework.
Model the Domain puts retry and reconnect decisions in explicit states with closed outcomes.
Sequence Work into Verifiable Units makes the runtime upgrade the first independently verified slice.
Test Behavior, Not Implementation retains real persistence, authority, and lifecycle proof after policy test migration.

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

The Poteto plan template requires ten live assignments and the repeated verification rule for each PR.
These assignments reuse existing smoke coverage. They do not require ten new permanent test fixtures.
Generic smoke proves binary regression checks only. It does not establish the refactored policy behavior.
The three affected-path probes are enabling work. Reuse existing fixtures and reject a new test framework.
Count their maintenance cost when assessing the actual reduction. This plan promises no reduction in total review effort.
Queue reviews within actual agent capacity. Run native smoke lanes serially because cleanup matches the daemon binary path.
Keep ports, process ownership, homes, and build outputs isolated.
If the control driver or evidence capture cannot exercise a boundary, report the gap without replacing it with a screenshot.

The cost thresholds are proposed acceptance gates. No runtime reduction or performance result exists yet.
Run the Benchmark Checklist before collecting or interpreting samples. Diagnose noisy or incomparable samples rather than discarding inconvenient runs.

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
