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

## Tool Execution Test Fixtures

Tests that use `DispatchingToolExecutor` must pass the same argument validation as a model call.
Supply `_rationale` when the test needs a valid call. Omit it only when the test checks rejection.
Use the test's private paths in both the execution context and the access policy.
Before testing a later effect, assert the required receipt or result from the real executor.
An error string can expose an invalid fixture before the intended tool runs.
A `FakeToolExecutor` result does not prove that the real executor accepts those arguments.

## Focused Mutation Tests

All focused gates run in one job definition, `mutation-gates` in `pr_validation.yml`, on each pull request and merge group. They do not run on `dev` pushes.
The job has thirteen Linux matrix groups that run in parallel with the normal test matrix.
The job timeout is 25 minutes.
Each Stryker invocation builds the test project and runs the initial test run again. That fixed cost is about 1.5 to 2 minutes per invocation.
The mutant run itself takes under 1 minute for most spans. Group time is mostly the sum of the invocations of its gates.
Hosted runs from 2026-10-17 through 2026-10-23 measured these gate times (minutes):
The table retains earlier group measurements. Gate notes below describe later additions and current total costs.

| Group | Gates | Hosted time (about) |
|-------|-------|---------------------|
| `verb-chain` | `exact-verb-chain` (seven invocations, 53 mutants, 9 to 17.5) | 9.5 to 18 |
| `reminder` | `reminder-execution` (4.4), `literal-twin` (2.9), `stream-commit-gate` (2.5), `shutdown-notice-identity` (18 mutants, one invocation, 3.4) | 13.7 (estimate) |
| `authorization-and-directory` | `tool-authorization` (5.8), `approval-directory` (5.2) | 11.3 |
| `guard-and-authority` | `skill-manage-guard` (4.6), `no-program` (5.1), `input-one-authority` (3.2) | 13.3 |
| `shell-consent` | `shell-once-identity` (2.3 local), `compound-redirect` (2.2 local), `thinking-settings` (74 mutants, three invocations, 6.8 local) | 11.3 local; hosted time pending |
| `stop-proof` | `stop-proof` (81 mutants, two invocations, 6.9), `secrets-plaintext-leaf` (8 mutants, one invocation, 2 to 3, estimate) | 10 to 11 (estimate) |
| `shell-command-analysis-a` | 131 Security mutants and 27 Actors mutants | [11m28s](https://github.com/netclaw-dev/netclaw/actions/runs/40006783408/job/120596616567) |
| `shell-command-analysis-b` | 172 Security mutants | [11m03s](https://github.com/netclaw-dev/netclaw/actions/runs/40006783408/job/120596616477) |
| `shell-assignment-and-paths` | `shell-assignment` (6.2 to 9.4), `path-access` (2.4 to 3.2; six spans since #2554, one local run took 2.9 and a hosted run is not measured yet) | 9 to 13 |
| `config-read-and-admission` | `shell-config-read` (5.3 to 8.4), `tool-authorizer-order` (2.8 to 3.5), `mcp-artifact-admission` (2.2) | 10.5 to 14.5 |
| `session-reliability` | `tool-batch-readiness` (3.5 to 3.9), `headless-stop-rule` (3.3 to 3.5), `open-child-runs` (2.9 to 3.4), `local-control-proof` (about 1 locally), `stopped-turn-continuation` (10 mutants, one invocation, 2.5 locally) | 13.5 to 14.5 (estimate; hosted measurement required) |
| `child-owner-and-report` | one Actors invocation for the owner, control, report, close, and drain-scan spans; `chat-close-dispatch-expiry` (two mutants, one CLI invocation) | [8m14s](https://github.com/netclaw-dev/netclaw/actions/runs/40069130760/job/120798739243) |
| `loopback-host-origin` | `loopback-host-origin` (two invocations, 4.3 to 6.6) | 4.5 to 7 (estimate) |
| `child-consent` | `child-consent` (36 mutants, one invocation, about 3, estimate), `text-answer-guard` (15 mutants, two invocations, 3.6 local), `child-resume-gate` (55 mutants, one invocation, 3.2 local) | 9 to 11 (estimate) |

The curation gate targets only `sameNameMatch is not null` in `CurationRulesEvaluator`.
Run `./scripts/run-curation-same-name-mutations.sh` to verify this boundary.
The paired rules tests reject its one equality mutant, `sameNameMatch is null`.
The script requires exactly one target mutant with `Killed` status and no other active mutant.
An absent, ignored, uncovered, surviving, timed-out, or compile-error target fails the gate.
Reports appear in `artifacts/stryker/curation-same-name`.
The first local measurement took 128 seconds, including its initial build and tests.
The [prior group job](https://github.com/netclaw-dev/netclaw/actions/runs/40069130760/job/120798739117) took 7m07s.
The complete group took 10m59s after this addition, including setup and report upload.
Keep the group below 15 minutes on a fast runner.

The `verb-chain` and `reminder` groups were one group, `verb-chain-and-reminder`. That group took 24.5 to 25 minutes, and one run reached the timeout.
The `authorization-and-directory` and `guard-and-authority` groups were one group, `approval-and-authorization`. That group took 16.5 to 24.6 minutes on the same day and ran near the timeout.
Each gate there runs Stryker two or three times, so a Stryker run costs 2 to 3 minutes in this group.
The first CI run of the split took 9 minutes 37 seconds for `verb-chain` and 10 minutes 16 seconds for `reminder`.
A hosted run of 2026-10-18 put the two gates of #2527 part 2 into `verb-chain` and `guard-and-authority`. Those groups took 21 and 20 minutes.
`shutdown-notice-identity` took 3.4 minutes and `stop-proof` took 6.9 minutes there. `stop-proof` has its own group, and `shutdown-notice-identity` runs in `reminder`.
The `shell-analysis` group held three gates. A hosted run of 2026-10-18 took 25 minutes 18 seconds, and the job timeout cancelled it. The `shell-assignment-and-paths` group held three gates and took 21.5 minutes.
The `session-reliability` group held five gates and took 18.7 minutes. It now holds three gates, and `child-owner-and-report` holds the other two. The `child-approval-close` gate (#2525) joined that group later.
The `child-control` gate of #2524 runs in `child-owner-and-report` too. The group held five gates with `loopback-host-origin`.
The `child-consent` group is new (child-approval-events-and-parent-pause). The `child-owner-and-report` group is near its
budget, so the change adds a group. PR 2 of that change added the text answer guard gate. PR 1 of
durable-child-consent-wait adds the child resume gate, which replaces the resume gate of the cut PR 3.

A [hosted run on 2026-10-19](https://github.com/netclaw-dev/netclaw/actions/runs/39340209966/job/118359593287) measured 15 minutes 57 seconds for that group.
All five gates passed. The total includes checkout, tool restore, and report upload. It excludes queue time. The loopback gate took 4 minutes 15 seconds.

The prior `shell-command-analysis` group was still the longest group. Its security invocation (303 mutants) took 15.8 minutes in the slow run. The new A and B groups run 131 and 172 Security mutants. The A group also runs 27 Actors mutants.

The 2026-10-23 hosted run measured 11m28s for A, 11m03s for B, and 3m17s for the combined child group.
These totals include checkout, tool restore, and report upload. They exclude queue time.
The `reminder` group took 16.4 minutes in the same run. Do not add a gate to it.
The hosted runner speed varies, so the time of one gate changes by up to 5 minutes between runs.
The `child-owner-and-report` group took 1309 to 1457 seconds of the 1500 second limit, and one run (job 120137806585) reached the timeout.
The `loopback-host-origin` gate moved to its own group. It targets `Netclaw.Daemon` and `Netclaw.Configuration`, not child runs.
Keep each group at 15 minutes or less on a fast runner. Do not raise the job timeout.
Measure a new gate on a cold runner and update this table.
Each group runs its gates in sequence after one checkout and tool restore, and it reports every failed gate.
The shared Actors mutation configuration stops if its initial test run fails.
Do not accept a mutant result from a run whose initial tests fail.
The `shell-consent` group contains the source-identity and compound-redirect gates. Measure its hosted cost on the next pull request.
The 2026-10-23 local run took 2 minutes 18 seconds and killed its one selected source-identity mutant.
Add a gate to the lightest group when its measured cost fits the timeout and the Windows test duration.
Use another group under the same job definition when no existing group has that capacity.

Focused mutation tests prove that deterministic tests reject a specific unsafe
change at a security, authority, or load-bearing reliability boundary. They do not measure general code
coverage. They do not replace positive and negative behavior tests.

### Current Targets

| Target | Protected claim | Expected mutants | Command |
|--------|-----------------|------------------|---------|
| `ChatClientActor.Start` (`// chat-close-dispatch-expiry:begin` to `end`) | Issue #2642, PRD-004: a held bind reply permits text dispatch at 1.5 seconds after Close, but refuses it at two seconds | 2 killed | `./scripts/run-chat-close-dispatch-expiry-mutations.sh` |
| `PathAccessPolicy.AddSessionRoots`, `PathAccessPolicy.IsReadableByAudience`, `PathAccessPolicy.GetEffectiveAccess`, `PathAccessPolicy.IsInOtherTemporaryKeyFolder`, and `ManagedTemporaryEnvironment.CheckFolder` | Only a Personal context receives shared session roots; a reviewed phrase uses the read authority of the audience, attended or not (D2), only for a fully qualified host path of the shell's own style that is not protected; issue #2554: a Personal read or attach follows the write profile and no other audience, operation, or write mode does (write `All` gives `All`, write `Roots` adds its roots, write `None` adds nothing, and a read profile of mode `All` stays `All`); the managed temporary key folder is a session root and the shared base is not; a Team or Public root over the base does not reach another key folder; the shell launch is refused when the base or the key folder is missing, a link, owned by another user, or open to a group or others | 30 killed | `./scripts/run-path-access-mutations.sh` |
| `ToolAccessPolicy.AdmitMcpAudience` | Server and tool audience grants precede approval | 2 killed | `./scripts/run-tool-authorization-mutations.sh` |
| `ToolAccessPolicy.ScreenHardDeny` | A shell hard denial precedes approval | 1 killed | `./scripts/run-tool-authorization-mutations.sh` |
| `ThinkingDialectMap.OfferedLevels`, `ThinkingDialectMap.Apply`, `ThinkingDialectMap.ApplySuppression`, `OutputLimitResolver.Resolve` (precedence and clamp arithmetic), and the pipeline reuse of `RoleBasedFailoverRouter` (`PipelineKey`) | model-output-and-thinking-settings D2, D4, D5, D8: a level is offered only when the dialect and the capability allow it, each missing level has a reason, a level that the dialect cannot send is rejected and never mapped to another level, each dialect writes its documented wire fields, the suppression intent writes Off only for a body-field dialect, a call-site limit beats the model limit, which beats the default, and the default is min(32000, model max, window - prompt - margin) with a 1024 floor. The router shares a pipeline only for the same provider (without case), model ID, and settings. `EstimatePromptTokens` and `ApplyThinking` are private members of the model client that only the wire rows reach, so they stay outside the gate. `ExpressibleLevels`, the rejection message of `Apply`, and the floor-warning flag are outside the spans: Stryker reports their mutants as compile errors, the tests check only part of the message, or they are equivalent | 74 killed (47 + 17 + 10) | `./scripts/run-thinking-settings-mutations.sh` |
| `OneTimeApprovalKeys.Create` source identity condition | Shell `Once` consent covers only the exact case-sensitive source. Equal candidate facts do not cover changed source. | 1 killed | `./scripts/run-shell-once-identity-mutations.sh` |
| `ShellGrantCandidateResult.IsFor` | Approval evidence keeps the requested candidate facts | 1 killed | `./scripts/run-tool-authorization-mutations.sh` |
| `ShellPolicyEvaluation.CandidateState.ValidateActorEvidence` | Actor evidence cannot replace existing candidate coverage (`Coverage != null`) | 1 killed | `./scripts/run-tool-authorization-mutations.sh` |
| `ToolAuthorizer` shell rule order (hard deny, trusted root, covering grant) | No rule can move ahead of an earlier rule: hard deny and today's trusted-root check precede a covering grant | 3 killed | `./scripts/run-tool-authorizer-order-mutations.sh` |
| Shell analysis, denial-only, tree effects, and reviewed-safe gates | Parser-proved regions and authored diagnostic syntax preserve hard denials; only bounded audited non-path values and consistent non-link-following tree facts can use reusable approval; a control-character word gets only the ancestor scope of its clean text; only a word below an absent top-level directory loses its path scope; an unresolved command is one exact candidate, and only decision D1 (an unknown operand with a safe phrase or a grant for anywhere, attended or not) covers it; a glob word gets the decision of each protected path that it can match (D5), and its link walk stays inside the covering directory; a bound value gets the hard-deny decision of its literal twin; a Bash test builtin is data only with proved operand values without `[`; a data command keeps its assignment digest unless each operand is proved data, and only such a data command with no redirect keeps its normal candidate after an unknown directory; a variable word that is not a path word is an unknown operand, so a folder grant cannot cover a loop or an assignment value outside the folder; an option value that the parser splits from its option (`--name=value`, `-p:Name=value`) and that can leave the working directory gets the scope of a path word with the same text, so a folder or repository grant cannot cover it; fixed text on stdin (a quoted heredoc or a proved here string) is data only on stdin and only for a receiver that is not a shell and has no argument that can name one | 330 killed | `./scripts/run-shell-command-analysis-mutations.sh` |
| Shell assignment identity, wrapper fallback, wrapper child source, hard-deny screen, syntax reconciliation, host mode, prompt rollback, Bash sanitation, and Bash launch-value digest | Reusable grants require exact facts, fallback wrappers and wrappers with an assignment prefix must stay one-time, a wrapper child source is the decoded argument value, unresolved Bash source and each list element meet the hard-deny screen, versioned prompts must fail closed, and strong modes require the reviewed launch contract; only an assignment that can reach the program qualifies a grant, a Bash data command keeps every assignment, and the parser names come from the same launch snapshot; an exact effective Bash value can qualify a digest when authored text is unknown, while PowerShell keeps the authored-value check (F3) | 84 killed | `./scripts/run-shell-assignment-mutations.sh` |
| Filesystem authority folder membership, repository identity, repository persistence, the folder of a new grant, and the link target scope | Folder and repository grants require candidate scope, identity, registration, and containment; a folder grant trusts its own root and refuses a link below it; a `..` after a link makes the shell scope unresolved; a word that names a link also has the scope of its final target, and a target after a `..` that leaves a link is unknown (#2375); a new folder grant uses the directory where its occurrence runs | 24 killed | `./scripts/run-approval-directory-mutations.sh` |
| `ReminderManagerActor.HandleExecutionOutcomeAsync` | Only the current attempt can settle; the manager replies after settlement | 2 killed | `./scripts/run-reminder-execution-mutations.sh` |
| `ActiveExecutionTracker.TryRemove` | Only the current owner can remove its guard; cleanup removes that guard | 2 killed | `./scripts/run-reminder-execution-mutations.sh` |
| `SessionInputFlow.PrepareAdmission` drop decision (`TryFindWaitingFire`, `TryReadFireKey`) and `ReminderFireKey.TryParse` | Rule 3: a session drops a newer fire only while an earlier input of the same reminder waits (the definition id matches, the flow's own queue holds that input, and only reminder-kind inputs count; a journal row that nothing will run never blocks); text that is not a fire key is never dropped and never blocks; the parser refuses any text but `{id}:{digits}` | 21 killed | `./scripts/run-reminder-execution-mutations.sh` |
| `ReminderManagerActor.TombstoneMatches`, `ReminderManagerActor.SettledOccurrenceIsCurrent`, and `ReminderManagerActor.TryRefuseStaleOccurrenceAsync` | A matching tombstone (a one-shot with the same fire time to the millisecond) wins over its definition, and a tombstone never matches a recurring definition; a settlement ends the definition only when its fire time equals the due time of the settled occurrence to the millisecond, so a definition that another save replaced during the run stays; a fire runs only when its due time equals the fire time of the definition, so the occurrence of an earlier definition is refused, and the fire handler returns after the refusal | 8 killed | `./scripts/run-reminder-execution-mutations.sh` |
| `McpArtifactMaterializer.TryAdmit` | Scanner approval and verified MIME both precede MCP artifact storage | 4 killed | `./scripts/run-mcp-artifact-admission-mutations.sh` |
| `SkillManageTool.GuardMutationTarget` and the filesystem authority link and protection results | A skill mutation cannot follow a link, write a protected path, or skip the atomic-write temp file | 5 killed | `./scripts/run-skill-manage-guard-mutations.sh` |
| `ToolAccessPolicy.ReadOnlyOccurrences`, the read relaxation in `ToolAccessPolicy.EnforceKnownShellPaths`, `PathAccessPolicy.EvaluateShellReadPath`, `FileSystemAuthority.HoldsReadProtectedPath`, and the read-operand exemption of the `ToolPathPolicy` text screen | Decision D6: a read-only shell program (`cat`, `head`, `tail`, `wc`, `grep`, `jq`, `diff`) with bounded arguments can read one exact config file; only a write-protected path gets read protection; a redirect that writes keeps write protection; a directory operand that holds a read-protected path, a plain word that names an entry, a glob, a brace or `$'...'` word, a `..`, the config directory itself, and program text that names it in any spelling (`//`, `/./`, `name/../`, split quotes) stay denied | 58 killed | `./scripts/run-shell-config-read-mutations.sh` |
| `ToolApprovalEntryComparer.CoversCommandWords`, `ShellPolicyCoordinator.SelectCommandWordsCorrection`, `ShellApprovalMatcher.TryResolveProgramPath`, `ShellProgramPath.MatchesLegacyRelative`, `ShellApprovalMatcher.ProjectCommandWords`, `ShellGrantFileWords.TryFindEntry`, and `ToolPathPolicy.PlainWordLinkReachesDeniedPath` | A verb grant (two or more words) covers its command words and any later words, and a program-only grant covers its word alone, so a `gh` grant does not cover `gh auth logout`; an empty grant covers nothing; the matcher and the store hygiene use this one rule; Unknown command words get a rewrite correction; a program path names its file (R1), so a `./tool` grant does not cover another file named `tool` or `mytool`; a word after the verb slot that names an existing file or directory leaves the command words and becomes a path scope, while the program word, the verb slot, a link, a word without a file, and a word that is not one entry of the directory stay; a plain word that names a link to a protected path is denied, command word or argument; the store and the doctor use the same rule | 53 detected | `./scripts/run-exact-verb-chain-mutations.sh` |
| `ShellCommandAnalysis.TryResolvePlainFileTarget` and `ToolAccessPolicy.ScreenNoProgramRedirects` | Only complete, exact, canonical plain-file targets qualify for the no-program exemption. Unknown targets, forbidden reads, and denied file tools block the call. The separate `shell-once-identity` gate proves exact source identity. | 33 killed; 2 duplicate block removals | `./scripts/run-no-program-mutations.sh` |
| `ShellPolicyProjection.HasValidCompoundCandidate` and compound path checks in `ToolAccessPolicy.ScreenShellTrustZone` | A source binder must name an owner in this call; a redirect target must pass its path rule; an input may use the read roots while an output still needs the write roots. | 5 killed | `./scripts/run-compound-redirect-mutations.sh` |
| `BashLiteralTwinSlices.Apply` and the denial check of `ToolAccessPolicy.ScreenScopedSlices` | Decision F1: the strictest literal twin result decides a call. The candidates of every twin replace the candidates of their source command, an unresolved source keeps its exact answer, twins without their source command fail loudly, and one denied twin denies the call | 9 killed | `./scripts/run-literal-twin-mutations.sh` |
| `SessionInputFlow.DrainBufferedBatch` (turn owner choice), `SessionInputFlow.JoinsTurn`, the person check of `SessionInputFlow.AdvanceAfterToolBatch` and `SessionInputFlow.AdvanceAfterCompaction`, and the automation check of `SessionToolWorkflow.PlanNewInputAbandonment` | A turn has one requester: a drain after a turn adopts the authority of the first buffered input, a drain inside a turn keeps the turn's authority, and only inputs of that requester join the batch; an Operator or TrustedInternal person who waits ends an automation turn at the tool-batch end and at the tool-loop compaction end, and an UntrustedExternal person, a child result notice, and a replay do not; automated input waits behind an open approval, and only input from a person closes it; a child result notice is automated input by its kind, although its principal is a person | 21 killed | `./scripts/run-input-one-authority-mutations.sh` |
| `LocalControlPairingProofValidator.ValidateAndConsume` operation check and the result of `LocalControlRequests.AuthorizeAsync` | A local-control proof grants only its own operation: a payload with the provider OAuth result purpose and the pairing operation byte is refused; a request without a valid proof is refused, and only a refused request counts against the per-source limit, so a flood of refused requests cannot block a valid proof | 4 killed | `./scripts/run-local-control-proof-mutations.sh` |
| `StreamCommitGate` | An attempt commits only at its first real output; id-bearing lifecycle updates are held, role-only keepalives pass through, and held updates are released on commit or clean completion | 8 killed | `./scripts/run-stream-commit-gate-mutations.sh` |
| `LoopbackHostOriginPolicy` (the Host and Origin rule and `IsEmptyOrPortSuffix`), `LoopbackHostName.IsStrictLoopback`, and the refusal call in `LoopbackAuthenticationHandler.HandleAuthenticateAsync` | Loopback operator trust needs a loopback Host name, and an `Origin` header must equal `http://` plus the Host: a foreign page cannot reach the daemon by DNS rebinding or a cross-site request | 41 killed | `./scripts/run-loopback-host-origin-mutations.sh` |
| `ActiveToolBatchTracker.CanComplete`, `SessionToolWorkflow.ValidateResponse`, `SessionModelContext.IsCurrentInspection`, `SessionState.Apply(TurnRecorded, bool)`, `SessionExecutionLifetime.AcceptModelStop`, `SessionExecutionLifetime.ObserveReapReply`, `SessionInputFlow.DrainBufferedBatch`, and `TurnStateTracker.CountRefusedRepeat` | A batch waits for every result and task completion; another sender cannot approve a call; an old inspection cannot change a later call; terminal history adds the user exactly once; an old model stop cannot settle a new call; one accepted reap reply requests only one journal commit; fresh buffered input starts a new turn; refused repeats 1 to 4 get the nudge, repeat 5 gets the escalated nudge, repeat 6 ends the turn, and a new call does not count | 19 killed | `./scripts/run-tool-batch-readiness-mutations.sh` |
| `SessionExecutionLifetime.ContinuesStoppedTurn` (`// <stopped-turn-continuation>`) | Issue #2632: a restart drain keeps a turn that stopped after its tool batch (one continuation input) only when no text reached the person, no other input waits, no overflow replay waits, no background job lives, each tool call has a recorded result, and the turn has a channel type. Each fact alone refuses the continuation. A wrong yes resumes a turn over an incomplete history or starts a killed job again; a wrong no loses the turn | 10 killed | `./scripts/run-stopped-turn-continuation-mutations.sh` |
| `HeadlessChannel` stop rule (`EndsRun`) | `netclaw chat -p` ends a run only on a failed turn or on the first completed turn that reports no open child run; a skipped turn with an open run and a turn with one of two open runs each get the right decision (SPEC-004, "Waiting for child runs"). The gate does not cover the missing-field case: `SessionOutputDtoMapper.FromDto` maps a missing field to an empty list, outside the mutated span, and ordinary tests cover it | 3 killed | `./scripts/run-headless-stop-rule-mutations.sh` |
| `SessionState.GetOpenChildRunIds` and `SessionState.GetExecutableChildRuns` | The list in `TurnCompleted.OpenChildRunIds` holds every run that has no notice, or whose notice input is still pending, and no run that the legacy reader quarantined, in acceptance order. A run missing from the list ends a headless run early and loses the child result. A run that stays in the list makes the run wait for ever (SPEC-004, "Waiting for child runs") | 4 killed | `./scripts/run-open-child-runs-mutations.sh` |
| `SecretsFileWriter.IsPlaintextLeaf` and `SecretsFileWriter.ShouldEncrypt` (`// <secrets-plaintext-leaf>`) | secrets.json encrypts every string leaf except `Providers.<name>.OAuthTokenExpiry`, a path of exactly three segments that matches without case. A mutant that encrypts the date stops the daemon at startup. A mutant that skips another leaf leaves a credential unencrypted at rest | 8 killed | `./scripts/run-secrets-plaintext-leaf-mutations.sh` |
| `SessionToolWorkflow.IsChildConsentPaused`, `SessionToolWorkflow.RefusesChildStart`, the pause hold of `SessionToolWorkflow.PlanNewInputAbandonment`, the pause rules of the `SessionInputFlow.DrainBufferedBatch` drain (`PassesPause`, `IsHeldByPause`, `MoveHeldInputBehind`), the hold rule `ChildConsentPause.Holds` that the drain and the recovery share, and `TurnContext.IsChildConsentNotice` | Design D5 of child-approval-events-and-parent-pause: an open child consent request pauses the session, and an answered request does not. While paused, a `spawn_agent` start is refused and a slash start of a person is accepted; automated input and a child result notice wait in Ready and in the drain, never join a running turn, and run after the last close; a person input runs; a replay continues its turn. An approval notice needs the principal `VerifiedAutomation`, the authenticity `LocalProcess`, and the source kind and sender `child-consent`, so a webhook cannot pose as one; a notice is never held, never joins a turn, and nothing joins a notice turn | 36 killed | `./scripts/run-child-consent-mutations.sh` |
| The guard of `SessionToolWorkflow.TryResolveTextResponse` and the count rule of `PendingApprovalLookup.Resolve` | Design D7 of child-approval-events-and-parent-pause: when a child prompt is among two or more prompts that the sender can answer, a text answer resolves none, and the session and the channel tell the sender to use the buttons; text that answers no prompt keeps the option-unavailable reply; with parent prompts only, the session resolves the latest prompt and the channels the earliest | 15 killed | `./scripts/run-text-answer-guard-mutations.sh` |
| `TurnContext.HasSameRequester`, `SessionState.RequireNoticeRun`, restart-resume partition, `TurnContext.CanControlChildRunOf`, reminder report and retry state, child approval close, and the continuing-turn notice scan in `SessionInputFlow` | These targets protect child ownership, notice admission, restart order, child control, one-time reminder reports, retry links, and approval cleanup. The continuing-turn scan skips a queued notice while it finds a replay or owner input. | 74 killed | `./scripts/run-child-owner-and-report-mutations.sh` |
| `ChildParkRules.AcceptPark`, `ResumeGate`, `RecoveryAction`, `ParkFits`, `CountsAsUnreported`, `RejectedEventEnds`, `IgnoredBySnapshot`, `LoopGuardDamage`, `RequesterCanAnswer`, and `ResumeStart` (`// <child-resume-gate>`), and `SessionExecutionLifetime.WaitsForChildren` (`// <child-drain-wait>`) | durable-child-consent-wait: only the owned child of an `Accepted` run opens prompts, a cancelling run ends cancelled, and any other sender gets the stop signal; a parked run resumes only when every request has an answer and no restart drain is pending. durable-child-park-recovery: recovery records a running child as lost and keeps or resumes a park; a park above 4 MiB is refused; a park with an open request does not keep the session in memory; a rejected record ends only the lifecycle that it expected; a child request does not block a snapshot and a parent request does; a damaged loop guard is found by each episode and named. running-child-drain-park: a drain park (no request) needs no requester who can answer, and a consent park still needs one; a resumed child starts with a model call only for a drain park, runs its parked calls only when each request has an answer, and otherwise ends; the drain acknowledgement waits only while a drain is requested, a live child exists, and the child wait has not ended | 55 killed | `./scripts/run-child-resume-gate-mutations.sh` |
| The identity check of `POST /api/lifecycle/shutdown` (`// shutdown-identity:begin` to `end`) | Rule 1 of #2527: the daemon announces its stop only for a notice with its own PID and its own home. A wrong PID or home gets 409 and no alert; a missing or malformed value gets 400; a folder inside the home is not the home | all tested mutants killed (at least 18) | `./scripts/run-shutdown-notice-identity-mutations.sh` |
| The proof in `DaemonManager.StopAsync` before the signal (`// stop-proof`, `// stop-notice`, `// stale-pid`) and the home comparison (`// home-match`) | Rule 2 of #2527: a PID gets a signal only when the daemon at the port of this home answers 200 with that PID and this home, or when it does not answer (or answers 200 with no `home`) and the environment of the process shows this home, read as that process reads it (`HOME` and `NETCLAW_HOME` of the target, never of the CLI). A 409, or a 200 with another PID or home, is never overruled; two folders that a `..` in a link target could join are two homes; a home that differs by case, by a folder level, or by a link is another home; a stale PID file signals nothing | all tested mutants killed (at least 32 in `DaemonManager`; at least 49 in `HomeIdentity`, where a `Timeout` counts as killed) | `./scripts/run-stop-proof-mutations.sh` |

Run the path-access check locally:

```bash
./scripts/run-path-access-mutations.sh
```

The script tests 30 mutants in six spans:

- the shared session-root boundary and the read branch of the reviewed-safe path check (two
  mutants each). `Reviewed_shell_path_uses_the_read_authority_of_the_audience` kills the branch
  mutants: a relative path and a protected path must not qualify, and an attended and an
  unattended read get the same decision (D2).
- the effective profile of a Personal read or attach (`GetEffectiveAccess`: the Personal check,
  the `Read` and `Attach` operation check, the write-mode check, and the root union). The kill
  list is in `PersonalReadReachMutationTests`.
- the key folder of the managed temporary location in `AddSessionRoots` (one block-removal mutant). The
  shared base must not be a root. `Key_folder_is_a_root_and_the_base_is_not` kills it.
- the other-key check of a Team or Public root over the base (`IsInOtherTemporaryKeyFolder`). The
  deny compares without case and the own key folder with case.
  `A_root_over_the_base_does_not_reach_another_key_folder` kills the six mutants.
- the folder checks of `ManagedTemporaryEnvironment.CheckFolder` (a missing folder, a link, a
  foreign owner, a group or other bit). The linked `ManagedTemporaryFolderCheckTests` kill them.

The helper `BoundedRoots` is outside the mutated spans. It ignores a leftover `Roots` list in a
profile of mode `None`. No test in the gate has a profile of mode `None` with leftover roots,
so a mutant of that helper would survive. Add such a test before you add the helper to a span.
The job fails unless all 30 mutants die.
The approval taxonomy PR 1 run took 3 minutes after package restore.
The local prototype took 1 minute 28 seconds after package restore.
A cold CI runner should take two to four minutes.

The harness uses xUnit 2 because Stryker's VSTest adapter does not support xUnit 3 correctly.
The script requires `perl` and `jq`, which the Linux CI image supplies.

### Chat Close Dispatch Expiry Gate

The xUnit 2 wrapper links `ChatClientCloseExpiryScenario` and the existing fake transport from the CLI tests.
Both frameworks use the same actor flow and preserve the RPC, cancellation, and receipt assertions.
The scenario holds the bind reply until Close takes effect.
It advances only the elapsed clock before it releases that reply.
The Akka `TestScheduler` stays stationary, so `CloseDeadline` cannot hide a missing dispatch guard.
The positive row advances 1.5 seconds; the expired row advances two seconds.
The positive row confirms dispatch before the two-second deadline.

The parent removed only the `Start` expiry guard in a manual mutation check.
The expired row failed on an actual `SendMessage` RPC; the positive row passed.
That check justifies this narrow target. It does not establish a Stryker count or cost.

Run `./scripts/run-chat-close-dispatch-expiry-mutations.sh` after the required disk check.
On a shared host, hold the build lock through Stryker's initial SDK build.
Release it when Stryker prints `Initial test run started`.
Keep the lock free during mutation analysis and test waits.
The script uses the existing CLI mutation project and its Stryker configuration.
It selects only the elapsed-time comparison within the marked guard.
The two expected mutants replace `>=` with `<` and `>`; both must have status `Killed`.
Missing, extra, untested, ignored, compile-error, and surviving target mutants fail the gate.
Other active mutants outside that comparison also fail the gate.
Stryker records ignored and compile-error mutants outside the selected comparison; these cannot satisfy this gate.

The finalized local gate killed both selected mutants in 177 seconds.
The finalized gate passes locally and in CI.
CI places this gate in `child-owner-and-report`; its complete hosted job took 8m14s.
The combined group must stay within 15 minutes on a fast runner.
The parent must measure the combined group before acceptance and move this gate if the total exceeds that budget.

### No Program Gate

Run the gate for commands that run no program:

```bash
./scripts/run-no-program-mutations.sh
```

The script runs Stryker two times. The first run selects
`ShellCommandAnalysis.TryResolvePlainFileTarget` (15 killed mutants). The resolver rejects an
incomplete redirect, an unknown target, and an invalid path. The second run
selects the redirect checks of
`ToolAccessPolicy.ScreenNoProgramRedirects` (18 killed mutants and one exact
ignored duplicate block-removal record). A mutant there lets `: < /etc/passwd`
run for an audience that may not read that path, or lets a redirect run when
`file_write` has the `Deny` mode. The script rejects any other ignored mutant,
compile error, or survivor in the selected span.
The same gate covers the transient fact for a program with composed scratch redirects.
The `shell-once-identity` target in the separate `shell-consent` group proves that a Once key keeps
the exact source identity when a redirect needs file consent. The source
candidate stays in the prompt for raw-source visibility and the command-length
check. It does not replace the source-key proof. The gate uses the existing
invocation and adds no Stryker build. A missing or duplicated span fails before
Stryker starts.

The local run took about 4 minutes after package restore, while another build
ran. CI runs it in the `guard-and-authority` group of the
`mutation-gates` job. Its report directory is `artifacts/stryker/no-program`.

### Compound Redirect Gate

Run the gate for the parser owner and the outer file effects of Bash compounds:

```bash
./scripts/run-compound-redirect-mutations.sh
```

The gate tests five mutants in three spans. The owner-membership check has two
mutants. `Compound_source_candidate_accepts_one_owner_from_a_multi_owner_call`
kills both. The target-denial check has one mutant. The launch-bound target test
kills it. The read selector has two mutants. The Team read-root control kills
both: input may use a read root, but output still needs a write root.

The script fails if a selected mutant does not run, survives, or causes a
compile error. The local run took 2 minutes 14 seconds after package restore.
The run used the xUnit 2 mutation project and tested 956 tests before mutation.
CI runs the gate with `shell-once-identity` in the `shell-consent` group.
The combined local cost is about 4.5 minutes. Hosted cost remains unmeasured.
The existing groups keep their headroom under the 15-minute fast-runner target.

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

### Local-Control Proof Gate

Run the local-control proof gate:

```bash
./scripts/run-local-control-proof-mutations.sh
```

The script runs Stryker one time on two spans. The first span is the
operation byte check of `LocalControlPairingProofValidator.ValidateAndConsume`.
A mutant that inverts the check lets a payload for one operation pass as
another operation when the purpose chain matches.
`LocalControlProofMutationTests` must kill it. The Data Protection purpose
chain binds the OAuth state. Stryker has no mutant for that binding, so the
deterministic validator and endpoint tests prove it.

The second span is the result line of `LocalControlRequests.AuthorizeAsync`
(3 mutants). One mutant lets a request with an invalid proof run its
operation. The other two count a valid proof as a refusal. A tunnel agent
forwards remote requests from loopback, so that mutant lets remote junk block
the host CLI. `LocalControlRequestsMutationTests` must kill all three.

The local run took about 1 minute after package restore. CI runs it in
the `session-reliability` group of the `mutation-gates` job. Its report
directory is `artifacts/stryker/local-control-proof`.

### Shutdown Notice Identity Gate

Run the shutdown notice identity gate:

```bash
./scripts/run-shutdown-notice-identity-mutations.sh
```

The script selects the identity block of the shutdown endpoint, between the
markers `// shutdown-identity:begin` and `// shutdown-identity:end`. A missing
or duplicated marker fails before Stryker starts. The tests in
`ShutdownNoticeIdentityMutationTests` run the real endpoint in a test host and
must kill every mutant: a mutant that accepts a wrong PID, a wrong home, a
folder inside the home, or a missing value would send a false `daemon.stopping`
alert or let a CLI of one home stop the daemon of another home. The gate needs
at least 18 mutants in the span, so a span that lost its checks also fails.

CI runs it in the `reminder` group of the `mutation-gates` job. It took 3.4 minutes on a hosted runner.

### Stop Proof Gate

Run the stop proof gate:

```bash
./scripts/run-stop-proof-mutations.sh
```

The script runs Stryker two times: on the marked spans of `DaemonManager.cs`
(`stale-pid`, `stop-notice`, `stop-proof`) and on the `home-match` span of
`HomeIdentity.cs`. A missing or duplicated marker fails before Stryker starts.
`StopProofMutationTests` start a stand-in daemon (a process that shows as
`netclawd`, a port, and a recording listener) and call `StopAsync` for real.
They need Linux. A span must not hold an `OperatingSystem` check or a retry
limit that no test can tell apart on the Linux runner; keep those outside the
markers, as the HTTP timeout, the `/proc` read, and the handling of a failed
notice are. The gate tested 32 mutants in `DaemonManager` and 49 in
`HomeIdentity`, and detected all of them. In `HomeIdentity`, a mutant that
removes the 40-link bound makes a link cycle spin. Stryker reports that as
`Timeout`, and the gate counts it as detected, as the exact verb chain gate does.
In `DaemonManager`, only `Killed` counts.

CI runs it in the `stop-proof` group of the `mutation-gates` job, with the secrets plaintext leaf gate.
The gate took 6.9 minutes on a hosted runner, and it made `guard-and-authority` take 20 minutes.

### Secrets Plaintext Leaf Gate

Run the secrets plaintext leaf gate:

```bash
./scripts/run-secrets-plaintext-leaf-mutations.sh
```

The script selects the policy in `SecretsFileWriter.cs` between `// <secrets-plaintext-leaf>` and
`// </secrets-plaintext-leaf>`. The span holds `IsPlaintextLeaf` and `ShouldEncrypt`. The selector
rejects a missing or repeated marker before Stryker starts. It runs one Stryker invocation, with
`Netclaw.Actors.MutationTests` as the test project and `Netclaw.Configuration.csproj` as the mutated project.

`SecretsFileWriter` encrypts every string leaf of secrets.json except `Providers.<name>.OAuthTokenExpiry`.
The configuration binder reads that leaf as a date and cannot read ciphertext. The path must have exactly
three segments, and the segments match without case. `SecretsPlaintextLeafMutationTests` cover these cases:

- the leaf stays plaintext, also with other letter case, and a JSON null stays null
- the same key name at depth one, two, or four, under another section, in an array, or as a flattened root key is encrypted
- a sibling of the exempt leaf is encrypted
- an `ENC:` value is neither encrypted again nor decrypted

Stryker makes eight mutants in the span: the length check, the early `return false`, the `&&`, two
string literals, the `||` in `ShouldEncrypt`, and two removals of `!`. The gate fails for a survivor, a compile
error, or a mutant outside the span. The count test sits in its own statement. Stryker rolls back a whole
statement when its `Count()` to `Sum()` mutant does not compile, and a count test inside one expression
would lose the length mutant.

The local run took 1 minute 33 seconds with a warm build cache. Hosted CI must measure the gate on a cold
runner. CI runs it in the `stop-proof` group of the `mutation-gates` job. Its report directory is
`artifacts/stryker/secrets-plaintext-leaf`.

### Stream Commit Gate

Run the streaming commit gate:

```bash
./scripts/run-stream-commit-gate-mutations.sh
```

The script selects the commit and hold logic in
`Netclaw.Daemon.Configuration.StreamCommitGate`. The OpenAI Responses adapter
yields content-free updates (`response.created`, `response.in_progress`,
`output_item.added`) before any output. An attempt must commit only at its
first update with real output. The gate requires eight killed mutants. They
cover these cases:

- an update that counts as substantive commits on real output
- an id-bearing lifecycle update is held, so no failed-attempt ids leak
- a role-only keepalive passes through without committing
- held updates are released on commit and on clean completion

The gate guards the keepalive branch. The ordinary unit test guards the
keepalive predicate body, which sits outside the mutation span.

This is the first reliability-boundary gate in the matrix. It guards a
load-bearing invariant in the retry and failover clients, not a security or
authority check.

The local run took about 2 minutes after package restore. CI runs it in the
`reminder` group of the `mutation-gates` job. Its report
directory is `artifacts/stryker/stream-commit-gate`.

### Loopback Host and Origin Gate

Run the loopback Host and Origin gate:

```bash
./scripts/run-loopback-host-origin-mutations.sh
```

The script runs Stryker twice. The first run selects two spans of `LoopbackAuthenticationHandler.cs`:

- the call in `HandleAuthenticateAsync` that fails authentication when the policy returns a refusal
- all of `LoopbackHostOriginPolicy`: `FindRefusal`, the loopback name test, and `IsEmptyOrPortSuffix`

The second run selects all of `LoopbackHostName.cs` in `Netclaw.Configuration`. That file holds the strict name test. The policy calls it directly, and the bind validator `DaemonExposureValidator.IsLoopbackHost` builds on it. Request admission depends on the strict test, so a widening of it is a security change. `LoopbackHostCouplingGuardTests` run with the gate, so a widening of the validator that lets a DNS name reach the Host check fails the initial test run.

A loopback source address does not prove that the operator made the request. A web
page in the operator's browser sends a foreign `Host` (DNS rebinding) or a foreign
`Origin` (cross-site request). The gate requires 41 killed mutants: 33 in the first run and 8 in the second. They cover
these cases:

- the handler fails authentication for a refusal and grants the Operator identity for an admitted request
- a foreign Host name is refused even when the Origin matches it
- an IP literal of `127.0.0.0/8` or `[::1]`, and `localhost`, are admitted
- a legacy IP spelling (`127.1`, `0x7f.1`, `2130706433`), an IPv4-mapped IPv6 literal, `0.0.0.0`, a DNS name that resolves to loopback, and a half-bracketed or whitespace name are refused
- a port suffix has one to five digits
- a loopback Host with a foreign, `null`, `https://`, other-port, or repeated Origin is refused
- a loopback Host with no Origin, or an Origin equal to `http://` plus the Host, is admitted
- the refusal reason names the failed header

The log write in `HandleChallengeAsync` is outside this gate. A deterministic
test covers it (`LoopbackRefusalLogTests`), and a mutant there cannot admit a request.

The selector rejects a missing or repeated source marker before Stryker starts.
The gate fails for a survivor or a changed mutant count. A mutant that becomes a
compile error changes the count. Stryker makes 4 such mutants in the selected
spans (the `origin.Count` comparisons and `Sum` replacements in `FindRefusal`).
The count of 41 excludes them.

The local run took 2 to 3 minutes after package restore. CI runs it in the
`loopback-host-origin` group of the `mutation-gates` job. Its report directory is
`artifacts/stryker/loopback-host-origin`.

### Session Reliability Gate

Run the session reliability gate:

```bash
./scripts/run-tool-batch-readiness-mutations.sh
```

The script selects the `CanComplete` expression in `ActiveToolBatchTracker`.
Stryker changes `ExecutionTaskCompleted && HasAllResults` to `ExecutionTaskCompleted || HasAllResults`.
The linked `ActiveToolBatchTrackerTests` source kills this one mutant when the task completes before all results arrive.
The script also selects the requester check in `SessionToolWorkflow.ValidateResponse`.
Stryker removes `!` from the `ApprovalButtonValueCodec.CanApprove` result.
The linked `SessionToolWorkflowRequesterTests` source kills this mutant when another sender answers a recovered prompt.
The script also selects the generation equality in `SessionModelContext.IsCurrentInspection`.
Stryker changes `generation == active` to `generation != active`.
The linked `SessionModelContextStaleInspectionTests` source kills this mutant when an old reply cannot alter a later call.
The script also selects the history choice in `SessionState.Apply(TurnRecorded, bool)`.
Stryker forces each branch of `userAlreadyInHistory ? closed.History : closed.History.Add(evt.UserMessage)`.
The linked `SessionStateTests` source kills both mutants when one branch duplicates a user or the other branch omits one.
The script also selects the call-ID inequality in `SessionExecutionLifetime.AcceptModelStop`.
Stryker changes `message.CallId != _modelCallId` to equality.
The shared `SessionExecutionLifetimeStopIdentityTests` source kills this mutant when a stop reply has the wrong call ID.
The script also selects the `true` literal in `SessionExecutionLifetime.ObserveReapReply`.
Stryker changes `reap.AwaitingCommit = true` to `reap.AwaitingCommit = false`.
The linked `SessionExecutionLifetimeStopPolicyTests` source kills this mutant when a duplicate reply arrives before journal commit.
The script also selects the replay check in `SessionInputFlow.DrainBufferedBatch`.
Stryker changes `!item.IsReplay` to `item.IsReplay`.
The linked `SessionInputFlowTests` source kills this mutant when fresh buffered input must start a new turn.
The script also selects the whole `TurnStateTracker.CountRefusedRepeat` method.
Stryker changes the increment, the cap, the two thresholds, and the repeat test.
The `RefusedRepeatCountMutationTests` source kills all 11 mutants when the count moves the escalation or the end by one response.
The linked `ProcessingWatchdogTests` source supplies the existing timer fake without an actor test host.
The selectors reject missing or repeated source markers before Stryker starts.
The gate requires one killed mutant in each of six single-expression spans, two killed history mutants, and eleven killed refused repeat count mutants.
It fails for no mutant, a survivor, or a compile error in a selected span.

The combined local calibration took 1 minute 56 seconds with a warm build cache.
The earlier readiness-only run took 5 minutes 44 seconds after tool restore.
The three-target local run took 1 minute 46 seconds with a warm build cache.
The five-mutant local run took 1 minute 47 seconds with a warm build cache.
The six-mutant local run took 1 minute 58 seconds on the lifecycle draft with a warm build cache.
The seven-mutant local run took 1 minute 48 seconds with a warm build cache.
The eight-mutant combined local run took 2 minutes 6 seconds with a warm build cache.
Hosted run `38448179312` took 15 to 21 minutes for the four existing matrix groups.
Its Windows test took 16 minutes 6 seconds. A target in any of those four groups would extend that critical path.
All eight source spans produce 19 expected mutants in one Stryker invocation in the `session-reliability` group.
The local run with the refused repeat span took 2 minutes 21 seconds with a warm build cache. The earlier run without it took 2 minutes 13 seconds.
Hosted CI must measure the combined gate on a cold runner.
Its report directory is `artifacts/stryker/tool-batch-readiness`.

### Headless Stop Rule Gate

Run the headless stop rule gate:

```bash
./scripts/run-headless-stop-rule-mutations.sh
```

The script selects the stop decision in `HeadlessChannel`. The source marks it with
`// <headless-stop-rule>` and `// </headless-stop-rule>`. The selector rejects a missing or repeated
marker before Stryker starts. The gate expects the decision in this form:

```csharp
// <headless-stop-rule>
internal static bool EndsRun(TurnCompleted turn)
{
    if (turn.Outcome == TurnOutcome.Failed)
        return true;

    return !turn.OpenChildRunIds.Any();
}
// </headless-stop-rule>
```

Stryker makes three mutants in that form: `==` to `!=`, `true` to `false`, and the removal of `!`.
A different form (for example one expression with `||`) gives a different mutant count, and the gate
fails until the owner of the gate reviews the new count.

`netclaw chat -p` sends one prompt, and a turn that calls `spawn_agent` returns `accepted` before the
child finishes. A wrong stop rule has two failures. The run exits at the first turn and loses the child
result, or it waits for a turn that never comes. The gate guards the rule with the real channel and a
fake daemon transport. `Netclaw.Cli.MutationTests` holds the tests. They cover these cases:

- an open run keeps the wait, and a turn that reports none ends the run
- one open run of two keeps the wait
- a failed turn ends the run even when a run is open
- a skipped turn with an open run keeps the wait
- a turn from an earlier daemon, without the field, ends the run (the test passes through the mapper default `dto.OpenChildRunIds is null ? [] : ...` in `SessionOutputDtoMapper.FromDto`; that default is outside the mutated span, so no `EndsRun` mutant depends on this test)

A mutant that makes the run wait for ever hits the fault ceiling of the tests. Stryker reports it as a
timeout. The gate counts a timeout as a detection. It fails for a survivor, a compile error, or a mutant
outside the selected span.

The local run took 10 minutes 30 seconds from a cold Release build. Stryker took 1 minute 59 seconds.
Hosted CI must measure the gate on a cold runner. CI runs it in the `session-reliability` group of the
`mutation-gates` job. Its report directory is `artifacts/stryker/headless-stop-rule`.

### Open Child Runs Gate

Run the open child runs gate:

```bash
./scripts/run-open-child-runs-mutations.sh
```

The script selects the producer of `TurnCompleted.OpenChildRunIds` in `SessionState.ChildRuns.cs`. The source marks
it with `// <open-child-runs>` and `// </open-child-runs>`. The span holds `GetExecutableChildRuns` and
`GetOpenChildRunIds`. The selector rejects a missing or repeated marker before Stryker starts.

Stryker makes four mutants in the span. The gate fails for a survivor, a compile error, or a mutant outside the
selected span. A different form of the span gives a different mutant count, and the gate fails until the owner of
the gate reviews the new count.

A headless run waits while the list is not empty. A wrong list has two failures. A run that is missing from the
list ends the run early and loses the child result. A run that stays in the list makes the run wait for a turn that
never comes. `OpenChildRunsMutationTests` in `Netclaw.Actors.MutationTests` builds a `SessionState` directly. It
uses no actor, no timer, and no delay. It covers these cases:

- a run in each lifecycle before `Closed` is open
- a `Closed` run is not open
- a run that the legacy reader quarantined is not open, and evidence that is not an unresolved child does not quarantine
- the list keeps acceptance order, then run id order

The local Stryker run took 1 minute 52 seconds. CI runs the gate in the `session-reliability` group of the
`mutation-gates` job. That group holds four gates. The first three gates take 10 to 11 minutes on a hosted runner.
The local-control proof gate adds about one minute locally. Hosted CI must measure the combined group. The child owner
and report-once gates run in the `child-owner-and-report` group. The open-child-runs report directory is
`artifacts/stryker/open-child-runs`.

### Child Owner Gate

Run the child owner gate:

```bash
./scripts/run-child-owner-and-report-mutations.sh
```

The script selects the body of `TurnContext.HasSameRequester`.
`LlmSessionActor.ControlChild` uses this check for child status and cancel.
Stryker inverts each of the six field comparisons and changes each of the five `&&` operators to `||`.
The linked `TurnContextRequesterTests` source must kill all 11 mutants.
A stranger that differs in one requester field must not own the child.
A later turn of the owner that differs only in per-turn fields must keep the child.
The script also selects the body of `SessionState.RequireNoticeRun`, the guard of a child result notice.
The linked `ChildNoticeReducerTests` source must kill all 21 mutants there.
A run gets one notice, only an ended run gets one, and the notice must be a user message with the recorded authority of the run owner.
The script also selects the requester loop of `SessionExecutionLifetime.ValidateRestartResume`, from the `HasSameRequester` check to `count++`.
The linked `RestartResumePartitionTests` source must kill all four mutants there.
A restart resume takes the leading inputs of one requester, and the inputs of another requester wait in the queue.
The selector rejects a missing or repeated source marker before Stryker starts.
The gate fails for a survivor, a changed mutant count, or a compile error in the span.

The span does not include the `HasSameRequester(right) &&` line in `TurnContext.HasSameAuthority` or the call in `ControlChild`.
Unit tests and actor tests cover those two lines.

The script collects selectors from the owner, control, report-once, and approval-close gates.
It adds the continuing-turn scan span and runs one Actors Stryker invocation.
The test filter is the union of those target tests and the new scan tests.
The report must contain exactly the expected mutants, and every target span must report `Killed`.
The report directory is `artifacts/stryker/child-owner-and-report`.

The scan tests prove these cases:

- a notice stays queued during a continuing turn with no other input
- owner input and replay can pass a queued notice, and the notice stays queued
- input from another requester does not pass a queued notice
- a notice starts at index zero at a new-turn boundary

### Child Approval Close Gate

The child approval close span runs in the combined child owner and report gate above.

The script selects the span in `SessionToolWorkflow.Children.cs` between `// <child-approval-close>` and
`// </child-approval-close>`. The span holds `ExpireRecoveredChildApprovals` and `CloseChildApprovals`. The selector
rejects a missing or repeated marker before Stryker starts.

Stryker makes five mutants in the span. The gate fails for a survivor, a compile error, a mutant outside the span, or
a changed mutant count. The count was six before PR 2 of child-approval-events-and-parent-pause: both methods now return
the entries that they close, so the early `return []` has no statement mutant. A different form of the span gives a different count, and the owner of the gate must review it.

The approval table of a session holds child approvals, and `AllowsSnapshot` needs an empty table. One rule closes a
child approval in live code: the cancel record or the terminal record of its run. The live Persist callbacks call
`CloseChildApprovals`. Recovery calls `ExpireRecoveredChildApprovals` in `RecoveryCompleted`, and it closes every child
entry that replay left open. `ChildApprovalCloseMutationTests` in
`Netclaw.Actors.MutationTests` builds the table directly. It uses no actor, no timer, and no delay. It covers these cases:

- a cancel record, a terminal record, and a lost terminal record close the pending and the resolved entries of the run
- a checkpoint and a prepared result close nothing
- a record closes only its own run (`run-1` is a prefix of `run-10`) and never a parent approval
- a record for a run with no entry changes nothing
- recovery closes every child entry and keeps a parent entry

The standalone local Stryker run took 2 minutes 14 seconds on a warm build cache.
The standalone report directory is `artifacts/stryker/child-approval-close`.
CI uses the combined report at `artifacts/stryker/child-owner-and-report`.

### Child Consent Gate

Run the child consent gate:

```bash
./scripts/run-child-consent-mutations.sh
```

The script selects five spans by marker pairs: `// <child-consent-pause>` in `SessionToolWorkflow.Children.cs` (4
mutants), `// <child-consent-hold>` in `SessionToolWorkflow.Recovery.cs` (3), `// <child-consent-drain>` in
`SessionInputFlow.cs` (19), `// <child-consent-predicate>` in `ChildConsentPause.cs` (3), and
`// <child-consent-notice>` in `TurnContext.cs` (7). The drain and the recovery call one hold rule,
`ChildConsentPause.Holds` (durable-child-consent-wait, design D5). The selector rejects a missing or
repeated marker before Stryker starts. The gate fails for a survivor, a changed count in a span, or a changed total.

The pause is actor-local and derived: an open child consent request in the approval table of the session. The actor
refuses a `spawn_agent` start through `RefusesChildStart`. `PlanNewInputAbandonment` holds automated input that
arrives in Ready. The drain holds it when a turn ends or a tool batch ends. `ChildConsentPauseMutationTests` in
`Netclaw.Actors.MutationTests` builds the table and the input queue directly, with no actor, no timer, and no delay.
`TurnContextChildConsentNoticeTests` covers the identification of a notice. The tests cover these cases:

- only an open child request pauses: an answered child request or a parent request does not
- a model start is refused while paused, and a slash start is accepted
- a reminder fire, a system job, and a child result notice wait in Ready; a person input runs
- a new turn skips the held input, and the held input runs once, in order, after the pause
- a held child result does not join the running turn of its owner
- an approval notice passes the pause and starts its own turn; a notice and a webhook that poses as one never share
  a turn
- a replay continues its turn while paused

The local Stryker run took 3 minutes 13 seconds. CI runs the gate in the `child-consent` group of the `mutation-gates`
job. Its report directory is `artifacts/stryker/child-consent`.

### Text Answer Guard Gate

Run the text answer guard gate:

```bash
./scripts/run-text-answer-guard-mutations.sh
```

The script runs Stryker two times. The first invocation mutates the span `// <text-answer-guard>` in
`SessionToolWorkflow.Approvals.cs` (`Netclaw.Actors`, 9 mutants). The second invocation mutates the span
`// <text-answer-lookup>` in `PendingApprovalLookup.cs` (`Netclaw.Channels`, 6 mutants). The selector rejects a
missing or repeated marker before Stryker starts. The gate fails for a survivor or a changed count.

Design D7 of child-approval-events-and-parent-pause, as the PR 2 review changed it: a text answer names no prompt. When
a child prompt is among two or more prompts that the sender can answer, the session and the channel resolve none and
tell the sender to use the buttons. With parent prompts only, the session resolves the latest prompt and the channels
resolve the earliest. `TextAnswerGuardMutationTests`, `TextAnswerParseOrderMutationTests`, and
`PendingApprovalLookupTests` (linked into `Netclaw.Actors.MutationTests`) cover these cases with no actor, no timer,
and no delay:

- a parent prompt and a child prompt, or two child prompts, resolve neither, with the nack `approval_use_buttons`
- two parent prompts resolve the latest (session) or the earliest (channel)
- one prompt of the sender resolves it, also one child prompt
- text that answers no prompt gets the option-unavailable nack, and text that answers one prompt gets
  `approval_use_buttons`
- the guard counts only the prompts that the sender can answer
- a button answer names its call ID, so two prompts do not make it ambiguous

The local Stryker runs took 2 minutes 27 seconds and 1 minute 11 seconds. CI runs the gate in the `child-consent`
group of the `mutation-gates` job. Its report directory is `artifacts/stryker/text-answer-guard`.

### Child Resume Gate

```bash
./scripts/run-child-resume-gate-mutations.sh
```

The script runs Stryker one time over two spans of `Netclaw.Actors`: `// <child-resume-gate>` in
`SubAgents/ChildRunPark.cs` and `// <child-drain-wait>` in `Sessions/Handlers/SessionExecutionLifetime.cs` (55 mutants
together). The target row above lists the rules. `ChildParkRulesTests` (linked into
`Netclaw.Actors.MutationTests`) covers them as tables, with no actor, no timer, and no delay. The selector rejects a
missing or repeated marker before Stryker starts, and the gate fails for a survivor or a changed count. CI runs the
gate in the `child-consent` group of the `mutation-gates` job. Its report directory is
`artifacts/stryker/child-resume-gate`.

### Child Control Gate

The child control span runs in the combined child owner and report gate above.

The script selects the body of `TurnContext.CanControlChildRunOf` and the property `IsReminderTurn`, from the start of the method to the end of the property.
`LlmSessionActor.ControlChild` uses this check for child status and cancel (issue #2524).
The `HasSameRequester` gate does not cover it, because the input join rule also uses `HasSameRequester`.
Stryker makes 16 mutants: the choice between the relaxed rule and the six-fact rule (two mutants), the two `||` operators between the three sides (two), each of the four kept facts (four equality mutants and three `&&` mutants), and the three parts of the reminder test (one equality mutant for each part and two `&&` mutants).
The linked `TurnContextChildControlTests` and `TurnContextChildConsentNoticeTests` sources must kill all 16 mutants.
The third side is the approval notice requester (live case of 22 October 2026): a notice turn controls a run of its own session.
A reminder turn that differs in a kept fact must not control the run, and the same holds for a person turn and a run that a reminder turn started.
A reminder turn must ignore the sender and the principal of the other turn.
A webhook turn (also one with the reminder source kind and the authenticity `Verified`), a turn with no source kind, and a person turn with the reminder source kind must not get the relaxation, as requester or as run starter.
Any other turn of the session, such as the delivery turn of a background job, gets it.
The selector rejects a missing or repeated source marker before Stryker starts.
The gate fails for a survivor, a changed mutant count, or a compile error in the span.

The span does not include `LlmSessionActor.HasSameChildRequester`, the call in `ControlChild`, or `HasSameAuthority`.
`ChildOwnerActorSpecs` covers the call path.

The standalone run killed 16 of 16 mutants in 2 minutes 7 seconds with a warm build cache.
The test filter for the combined CI gate includes both child control test classes.
CI runs one combined Stryker invocation for four targets in the `child-owner-and-report` group of the `mutation-gates` job.
The standalone run writes its report to `artifacts/stryker/child-control`.
CI writes the combined report to `artifacts/stryker/child-owner-and-report`.
Measure the group on a cold hosted runner and update the table.

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

### Input Authority Gate

Run the input authority gate:

```bash
./scripts/run-input-one-authority-mutations.sh
```

The script selects two spans in `SessionInputFlow`.
The first span is the turn owner choice in `DrainBufferedBatch`: `adoptsAuthority` and `owner`.
The second span is the join rule in `JoinsTurn`.
Stryker makes five mutants in the first span and three mutants in the second span.
The linked `SessionInputFlowRequesterTests` source must kill all eight mutants.
A follow-up turn must serve one requester, a running turn must admit only its own requester, and a replay must continue its turn.
The script also selects two spans in `SessionToolWorkflow.Recovery.cs`: the check `isChildNotice || IsAutomation(inputPrincipal)` in
`PlanNewInputAbandonment`, and the automation test in `IsAutomation`.
Stryker makes two mutants in the first span and one in the second. The linked `NewInputAbandonmentPlanTests` and
`ChildNoticeReducerTests` sources must kill all three.
Automated input (`VerifiedAutomation`, `SystemProcess`) must wait, and a person or a null principal must close the approval.
A child result notice is automated input by its kind: it waits whatever its principal is, because it carries the principal of the person who owns the child.
Stryker also makes an "or" to "and" pattern mutant in the principal set. That mutant does not compile, so the counts exclude it.
The script also selects three spans of the person check in `SessionInputFlow`: the call site in `AdvanceAfterToolBatch`,
the call site in `AdvanceAfterCompaction`, and the predicate `PersonWaitsBehindAutomation`. Stryker makes one mutant in
the first span, three in the second, and six in the third. The `ChildConsentPauseMutationTests` table must kill all ten.
An Operator or TrustedInternal person who waits must end an automation turn at a tool-batch end and at a tool-loop
compaction end. An UntrustedExternal person, a child result notice, a replay, and an overflow compaction must not end it.
In the predicate, the "or" to "and" pattern mutant and the three mutants of the `_context is { } owner` conjunction
do not compile, so the counts exclude them.
The selector rejects a missing or repeated source marker before Stryker starts.
The gate fails for a survivor or a changed mutant count. A mutant that becomes a compile error changes the count.
`TurnContext.HasSameRequester` itself is outside this gate.

The eight-mutant local run took 2 minutes 33 seconds with a warm build cache.
The ten-mutant local run took 2 minutes 3 seconds on a quiet host and 4 minutes 29 seconds while other builds ran.
The automation spans add two mutants and one linked test class. The change in run time is inside the noise between runs.
The eighteen-mutant local run with the person check took 2 minutes 29 seconds with a warm build cache.
The twenty-one-mutant local run with the compaction-end check took 2 minutes 38 seconds with a warm build cache.
CI runs it in the `guard-and-authority` group of the `mutation-gates` job.
Its report directory is `artifacts/stryker/input-one-authority`.

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

The same class pins decision D2. A stored grant covers a readable path outside
the project, attended or not, with the ordinary stored-grant reason. A bounded
(`Roots`) write profile keeps the trusted-root denial ahead of a covering grant
for both run kinds. Without a grant, an unattended call is denied with
`approval_required_unattended`. The negative controls stay denied: Auto mode
under a bounded profile, and a protected path with a grant.

The tests pick the host shell and a temporary root without links, so they also
pass in the normal Windows and macOS test jobs. The local run took about
2 minutes after package restore. CI runs it in the `config-read-and-admission` group of the
`mutation-gates` job. Its report directory is
`artifacts/stryker/tool-authorizer-order`.

### Exact Verb Chain Gate

Run the exact verb chain gate:

```bash
./scripts/run-exact-verb-chain-mutations.sh
```

The script runs Stryker seven times. The first run selects the length checks in
`ToolApprovalEntryComparer.CoversCommandWords` (11 mutants). A verb grant (two
or more words) covers its words and any later words, and a program-only grant
covers its word alone. A mutant that drops the one-word check lets a `gh` grant
cover `gh auth logout`. A mutant of the shorter-list check lets a grant cover
fewer words than it names, and a mutant of the empty check lets an empty grant
cover any call. The approval matcher and the store hygiene share this rule. The
second run selects the return of
`ShellPolicyCoordinator.SelectCommandWordsCorrection` (three mutants). A
mutant that drops the rewrite correction turns a bare-glob call back into a
prompt or a denial. The third run selects the return of
`ShellApprovalMatcher.TryResolveProgramPath` (four mutants). A mutant that
skips the join with the working directory lets a `./tool` grant cover any file
named `tool`. The fourth run selects the `/` boundary of
`ShellProgramPath.MatchesLegacyRelative` (one mutant). Without it, an older
`./tool` grant covers `mytool`. The fifth run selects
`ShellApprovalMatcher.ProjectCommandWords` (six mutants): a file word leaves
the command words, and a link word stays. The sixth run selects
`ShellGrantFileWords.TryFindEntry` (16 mutants). The seventh run selects
`ToolPathPolicy.PlainWordLinkReachesDeniedPath` and
`ToolPathPolicy.FindLinkWords`, the one loop that the screen shares with the
link scopes of #2375 (12 mutants): a plain
word that names a link to a protected path is denied, command word or
argument, and the program word does not count. `ExactVerbChainMutationTests` and
`ToolAuthorizerOrderMutationTests` must detect all of them. A missing or
duplicated span fails before Stryker starts.

The local run took about 1 minute after package restore. CI runs it in the
`verb-chain` group of the `mutation-gates` job. It takes about 14.5 minutes there,
because its seven Stryker invocations each build and run the initial tests. Its report
directory is `artifacts/stryker/exact-verb-chain`.

### Approval Directory Gate

Run the approval directory gate:

```bash
./scripts/run-approval-directory-mutations.sh
```

The script reuses the xUnit 2 harness.
It selects thirteen security source regions, three approval actor conditions, and the folder rule of the grant builder.
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
| Link scopes of a path word (`ShellApprovalMatcher.TryAddLinkScopes`, #2375) | 1 killed: remove the logical negation |
| Lexical scope of a path word that names no link (`ShellApprovalMatcher`) | 1 killed: remove the logical negation |
| Link scopes of a plain link word (`ShellApprovalMatcher`) | 1 killed: remove the logical negation |
| Link folder scope (`ShellApprovalMatcher.TryAddLinkScopes`) | 2 killed: remove the statement or use the link itself as the folder |
| Unknown target after a `..` that leaves a link (`FileSystemAuthority.FollowLinkChain`) | 1 killed: negate the test |
| Common identity across candidates (`RepositoryIdentity`) | 1 killed: remove the logical negation |
| Reciprocal worktree registration (`RepositoryIdentity`) | 1 killed: remove the logical negation |
| Persistence candidate resolution | 1 killed: remove the logical negation |
| Persistence common identity | 1 killed: remove the logical negation |
| Persistence worktree root | 1 killed: remove the logical negation |
| Folder of a new grant (`GrantBuilder`, `candidate.Directory ?? workingDirectory`) | 3 killed: swap the operands or keep only one side |

The script requires these counts at their exact source locations and 24 tested mutants overall (18 Security, 6 Actors).
It fails if a target is absent, survives, exceeds its time limit, or cannot compile.
The source selector rejects an absent or duplicate boundary before Stryker starts.
This protects the gate when the authorization code and diagnostic code contain similar conditions.

Twenty-three cases exercise the approval matcher and persistence gate with real directories and links.
They cover the grant root, normal descendants, sibling prefixes, traversal, relative paths, and candidate scope that differs from cwd.
One case proves that a grant root which is itself a link still covers its children (R3).
One case proves that a `..` after a link voids the grant, and that a `..` after a real directory or below a root alias keeps it.
One case proves that a folder grant covers a link word only when it covers the link folder and the final target (#2375). It uses a path word, with and without a file extension, on every host and a plain word on POSIX hosts.
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

### Reminder Report-Once Gate

The reminder report-once spans run in the combined child owner and report gate above.

The standalone script reuses the xUnit 2 harness of `Netclaw.Actors.MutationTests` and selects eight spans of `DeliveryRetryHandler`:

| Boundary | Expected mutants | Killed |
|----------|------------------|--------|
| `ReminderOf` turn check: the answer for a turn that is not the latest recorded turn | Reverse the turn comparison | 1 |
| `TryMarkReported` guard: wrong turn, no reminder, or already reported | Reverse a comparison, change a logical operator, negate the guard, or return true | 6 |
| `TryMarkReported` mark: the once-only mark | Set the mark to false, or return false | 2 |
| `RecordTurn` mark reset: a new recorded turn starts unreported | Set the mark to true | 1 |
| `Clear` retry-link end: a new input ends the link of a pending retry | Set the pending flag to true | 1 |
| `RecordTurn` retry-link end: the link reaches only the retry turn | Set the pending flag to true | 1 |
| `RecordRetry` retry-link start: a retry keeps the link of the turn that it repeats | Set the pending flag to false | 1 |
| `RetriedTurnReminder`: the link only while a retry is pending | Replace the condition with true or with false | 2 |

Each location must kill all of its mutants. The report must contain exactly 15 tested mutants.
`DeliveryRetryHandlerReminderTests` kills all of them. The mutation project links that class, and the
script adds no new test. The gate fails if a target is absent, ignored, survives, or cannot compile.
The selector rejects absent or duplicate source markers before Stryker starts.

This gate protects an alerting path, not an authority boundary. A mutant that makes `ReminderOf` ignore the
turn number would let a report for another turn name the wrong fire. A mutant on the mark would raise one alert for
each failed post of one reply, or none. A mutant on the retry link would hide the final failure of a retried
child result notice turn, or give a later person's turn the link of a reminder.
The standalone gate took 2 to 4 minutes in its hosted runs. Its local script took 114 seconds, which includes the Stryker build.
The standalone report directory is `artifacts/stryker/reminder-report-once`.
CI uses the combined report at `artifacts/stryker/child-owner-and-report`.

### Reminder Execution Gate

Run the reminder execution gate:

```bash
./scripts/run-reminder-execution-mutations.sh
```

The script reuses the xUnit 2 harness and selects these spans:

| Boundary | Expected mutant | Killed |
|----------|-----------------|--------|
| Manager outcome ownership | Reverse the execution-ID comparison | 1 |
| Manager acceptance reply | Remove the reply from `finally` | 1 |
| Tracker removal ownership | Reverse the execution-ID comparison | 1 |
| Tracker guard cleanup | Remove the dictionary removal | 1 |
| Reminder terminal identity | Reverse the `SourceReminderId` comparison | 1 |
| Drop decision call | Negate the drop test | 1 |
| Drop comparison | Change the queue test (`IsBuffered`), the key read, or the definition-id comparison | 5 |
| Incoming key read | Skip or invert the incoming fire key read | 2 |
| Reminder-kind check | Change the kind comparison or the parse condition of a stored input | 2 |
| Fire key parser | Change the null, separator, or digit checks | 11 |
| Tombstone match | Change the one-shot test, the fire-time comparison, or the operator that joins them | 3 |
| Settled occurrence check | Change the fire-time comparison of the settlement | 1 |
| Stale occurrence refusal | Negate the fire-path check of the due time, or flip its result | 2 |
| Stale occurrence return | Remove the `return` after the refusal, or negate its condition | 2 |

Each location must kill all of its mutants. The report must contain exactly 34 tested mutants.
`ReminderFireDropRuleTests` and `ReminderFireKeyTests` kill the five locations before the last two.
`OneShotEndMutationTests` kills the tombstone match, the settled occurrence check, and the stale occurrence refusal.
Each case there reads the files that the manager leaves behind, and none depends on a timer.
The gate fails if a target is absent, ignored, survives, exceeds its time limit, or cannot compile.
The selector rejects absent or duplicate source markers before Stryker starts.
Unrelated compiler errors do not count as tested mutants.

Seven settlement cases use the real manager, execution tracker, actor mailbox, definition store, and history store.
An isolated host supplies the local Akka.Reminders scheduler with an in-memory store.
The fixture observes execution IDs through the existing dispatch event and holds the session pipeline on a task.
Actor replies provide barriers. The tests use no delay or sleep to wait for state.
The fixture has two wait bounds. Do not merge them.
The 2-second bound applies only to a wait that a gate mutant can starve: the acceptance reply and a dispatch after a release.
A mutant must fail a test before the Stryker limit for each mutant (27.4 seconds in a local run), or the gate reports `Timeout`.
The 30-second bound applies to host start, health replies, the termination barrier, the first dispatch of a test, and the local execution.
Every gate mutant completes these waits. On a loaded Windows or macOS runner, their cold work took more than 2 seconds.

The stale-message cases complete attempt A, start attempt B, and replay A's completion or termination.
They require B's guard, history, failure count, and alerts to remain unchanged.
A current-termination control must record the failure and release its guard.
Success and failure cases require the correct acceptance ID, history, failure count, and subsequent dispatch.
A directory at the atomic-write path causes a real save failure; cleanup and the acceptance reply must still occur.

The terminal-identity case drives `ReminderExecutionActor` with a local test
pipeline. It sends a failed terminal from another reminder key, then a
successful terminal for the expected key. The first terminal must not settle
the execution. This target checks cross-turn output correlation; it does not
prove a durable completion receipt for a completed duplicate delivery.

These tests preserve PRD-008 SCHED-005 and SCHED-007.
See [the execution and settlement contract](openspec/specs/netclaw-scheduling/spec.md) and
[the history contract](openspec/specs/reminder-execution-history/spec.md).
The fixture injects internal outcome messages and synthetic envelopes.
It does not prove the child outcome producer, durable scheduler settlement, restart recovery, or external delivery.

Stryker 5.0.0 omits statement removal when the statement contains an `out` keyword.
It also filters the complete `finally` deletion when the acceptance-reply mutant exists.
Thus, the cleanup mutant targets the tracker dictionary, not the manager's `TryRemove` call.
An explicit local deletion of that call must also make the cleanup cases fail before this gate changes.

The five-mutant local run took 122 seconds after package restore.
The thirty-mutant local run took 132 seconds of Stryker time.
CI runs it in the `reminder` group of the `mutation-gates` job. It takes about 4.3 minutes there.
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

The script tests 330 mutants across execution-region accounting, denial-only
matching, tree traversal and root correspondence, bounded non-filesystem
values, data operands of output commands and test builtins, candidate extraction, approval
mode, path facts, and reviewed-safe policy. The job fails unless every mutant
dies.

Approval taxonomy PR 2 adds the Bash data-operand rule: a dynamic operand of
`echo`, `printf`, `:`, `true`, or `false` is data. Three mutants cover the
grammar and verb check and two cover its use in `CommandHasDynamicSyntax`.
`Dynamic_value_is_data_only_in_an_output_operand` and
`Power_shell_output_alias_keeps_a_dynamic_value_unresolved` kill them: a
substitution in an `echo` operand keeps the candidates, and a dynamic operand
of `cat`, a dynamic program word, a dynamic redirect target, or a PowerShell
`echo` stays unresolved. PowerShell keeps the bare `$?` rule (2 mutants);
`Power_shell_bare_status_output_keeps_static_candidates` kills them. The gate
kills 75 Security and 9 Actors mutants (3.5 minutes).

Approval taxonomy PR 3 adds `ShellApprovalMatcher.ResolveControlCharacterScope`
(4 mutants). A word with a control character, such as multi-line
`python3 -c` code, gets the deepest ancestor directory of its text before the
first control character. `Control_character_word_gets_its_clean_ancestor_scope`
kills the mutants. The gate now kills 79 Security and 9 Actors mutants.

Approval taxonomy PR 4 adds `ShellApprovalMatcher.HasAbsentTopLevelDirectory`
(3 mutants). An absolute word below a top-level directory that does not exist
on the host, such as the `gh api` route `/repos/o/r/actions/jobs/1/logs`, has
no path scope. The probe runs only when the working directory exists on the
host. `Absent_top_level_word_has_no_path_scope` kills the mutants. The gate
now kills 82 Security and 9 Actors mutants.

Approval taxonomy PR 5 (per-command judgment) adds 7 Security and 19 Actors
mutants. The Security targets are the directory guard of
`ShellApprovalMatcher.ExtractCommandCandidates` (a command after an unproved
directory change is exact) and the part of `CreateExactCandidate`. The Actors
targets are the D1 grant filter (`ShellPolicyCoordinator.KeepUnknownOperandGlobalGrants`),
the interactive switch (`ToolAccessPolicy.WithCommandCandidates`), the "Once"
only rule of `HasReusableShellPhrase`, and the D1 rule of
`ReviewedSafeShellPolicy`. `PerCommandJudgmentMutationTests` kills them with
the real authorizer: a folder or chat grant, an unknown redirect target, a
glob scope, a link, a known outside path, and an unattended call keep the
prompt or the denial. The data-operand target moved into
`ClassifyUnresolvedPart`. The gate now kills 89 Security and 28 Actors mutants.

Approval taxonomy PR 6 (owner decision D2) replaces the blanket kill denial
with `ShellCommandPolicy.DaemonProcessKillDenyPattern` (3 mutants). A kill is
denied only when an operand names the Netclaw daemon.
`Only_a_kill_that_names_the_daemon_is_hard_denied` kills the mutants: a kill
of a process ID or a test server stays allowed by the hard-deny list, and a
kill that names `netclaw` stays denied. The gate now kills 92 Security and 28
Actors mutants.

The ShellSyntaxTree 0.4.0-beta.17 update adds 74 Security mutants in three
targets. `GlobPolicyMutationTests` kills them on the Bash 5.2 host:

- Decision D5 (option A): `ToolPathPolicy.GlobMayReachDeniedPath` and the
  `ShellGlobScope` segment match. A glob word that can match a protected path,
  the default credential store, or a directory that contains one, gets the
  literal-path denial. A dot entry, a bracket expression, and case are covered.
- `ShellGlobScope.IsLinkContained`: the link walk for a glob in a directory
  segment, with its bound of 4096 directories.
- `ShellCommandPolicy.EvaluateEffectiveValues`: a word that reads a binding,
  such as `x=/; rm -rf "$x"`, gets the hard-deny decision of its literal twin.
  A loop variable checks each combination, and more than 256 combinations deny.

The gate now kills 166 Security and 28 Actors mutants.

Decision D2 (an unattended run uses the audience policy of a chat) removes the
attended-only condition of `ToolAccessPolicy.WithCommandCandidates`, and with it
one Actors mutant. `PerCommandJudgmentMutationTests` now shows that an
unattended call gets the D1 decision of a chat, and is denied where a chat
would prompt. The gate now kills 166 Security and 27 Actors mutants.

The test builtin rule extends two targets, `HasOnlyDataOperands` and
`ClassifyUnresolvedPart`. It adds four targets: `HasTestBuiltinVerb`,
`HasBoundedNameSafeValue` with `HasNoSubscript`,
`ShellVerbPolicyData.IsDataCommand`, and
`ShellApprovalMatcher.TryCreateAssignmentDigest`.
`Test_builtin_operand_is_data_only_with_a_bounded_value_without_a_subscript`
kills them: a proved operand without `[` is data, and an operand with `[`, an
unknown value, or a glob file name is not. A data command with a redirect keeps
its assignment digest. `Power_shell_test_word_is_not_a_data_command` kills the
Bash condition. The gate now kills 183 Security and 27 Actors mutants. The local
run took 9 minutes 45 seconds.

The review of #2344 adds `HasProvedDataOperands` and `IsOneDoubleQuotedWord`.
A Bash data command keeps its assignment digest unless each operand has a
proved value or is one double-quoted raw word (a test operand needs a proved
value without `[`). `Test_builtin_operand_is_data_only_with_a_bounded_value_without_a_subscript`
and `One_double_quoted_word_has_no_unescaped_inner_quote` kill the new mutants.
The gate now kills 211 Security and 27 Actors mutants.

The ShellSyntaxTree 0.4.0-beta.19 update replaces `IsOneDoubleQuotedWord` with
the parser fact `MayPathnameExpand`, and it adds `HasUnboundedPathnameExpansion`
and `HasGlobFreeAuthoredValue` as targets. `Unknown_word_that_can_glob_is_not_data`
kills their mutants: a quoted or glob-free word is data or keeps decision D1,
and an unknown word that can glob makes its command one exact candidate.
`Glob_free_authored_value_has_nothing_to_expand` kills the mutants of the glob
character check. An output operand no longer needs the separate proved-value
test, because a proved value without a glob character already passes. Owner
decision (#2349) exempts Bash data commands (`echo`, `printf`, `test`, `[`)
from the exact-candidate rule; the `echo` rows and the `git log -n $?` rows
kill the new mutants. `IsProvedValueDenied` is also a target:
`Decoded_word_gets_the_decision_of_its_proved_value` kills its mutants with
decoded ANSI-C paths. The gate now kills 214 Security and 27 Actors mutants.

The heredoc parity change (owner decision 2026-10-07) adds five targets (24
mutants): the heredoc and here-string arms of `HasUnresolvedRedirect`,
`HasFixedTextStdin` with `CanTakeFixedStdinText` and `HasShellReceiver`,
`MayNameScriptShell`, the expansion-mode check of `HasLiteralHereDocument`, and
`ShellVerbPolicyData.IsScriptShellProgram`. A quoted heredoc or a proved here
string on stdin is data. These forms stay unresolved:

- an expanding heredoc, an unknown here string, and another descriptor;
- a command with Unknown command words;
- a shell receiver, by the file name of each verb word;
- an argument with a part that is a shell file name, or with no proved value.

`Fixed_stdin_text_is_data_only_for_a_receiver_that_is_not_a_shell` and
`Fixed_stdin_text_fails_closed_for_an_argument_that_can_name_a_shell` kill the
mutants. The gate now kills 303 Security and 27 Actors mutants.

The script splits 303 Security mutants into A (131) and B (172). Group A also runs
the 27 Actors mutants. The expected counts must sum to 330.
The default local command runs both Security parts and the Actors part.
Use `--part a` or `--part b` to run one CI part locally.

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

The script tests 84 mutants across eighteen narrow targets.
It covers grant identity, wrapper fallback, wrapper child source, the hard-deny screen, prompt rollback, reviewed-safe exclusion, source spans, Bash host selection, and environment sanitation.
The prompt rollback target is `ConsentAnswerCodec.IsOffered` in `src/Netclaw.Actors/Authorization/Consent/ConsentAnswer.cs`.
It decides whether the prompt offered the selected option key before the key becomes a `ConsentAnswer`.
The job fails unless every mutant dies.

The 2026-10-23 local run took 5 minutes 22.73 seconds.
It killed 69 Security mutants and 15 Actors mutants, with no survivors,
timeouts, or test errors. The Bash authored-value target killed all three
mutants for the shell check, the `&&` condition, and the authored-value check.
CI allows 15 minutes for hosted-runner variance and report upload.
The report directory is `artifacts/stryker/shell-assignment`.
The authorization PR 4 re-run killed the same 41 Security and 15 Actors
mutants in about 4 minutes. The wrapper fallback target still covers the
parser-decoded child source.

The ShellSyntaxTree 0.4.0-beta.17 update keeps 56 Security and 15 Actors
mutants. The hard-deny screen target now also requires a complete parse: a
parse that stops at a `bash -lc` child after a `cd` that can fail screens each
list element again. The `Wrapper_child_source_is_the_decoded_argument_value`
input moved from a background list, which now parses, to arithmetic
expansion, which does not.

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
7. Use another parallel matrix group if the total group approaches its timeout or exceeds the Windows test duration.

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
| Case renamed | Needs an intended change with `renamedFrom`. The check compares the old `Result` with the new `Result` under the rules above. |

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
      "approvedBy": "<owner, when the rule needs it>",
      "renamedFrom": "<old case ID, only for a renamed case>"
    }
  ]
}
```

For a rename, the old ID must be in the baseline only, and the new ID must be in
the candidate only. A rename with no outcome change uses the same value for
`from` and `to`.

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
python3 scripts/authorization-corpus/run.py --base upstream/dev --jobs 1        # one decision lane
python3 scripts/authorization-corpus/run.py --base upstream/dev --head-adapter authorizer
python3 scripts/authorization-corpus/run.py --base upstream/dev --keep          # keep the head output and worktrees
python3 scripts/authorization-corpus/run.py --base REV --head REV --fresh-head  # two probes of one revision
```

How it works:

1. The script builds the corpus from every string literal in
   `src/Netclaw.Actors.Tests` and `src/Netclaw.Security.Tests` at a fixed
   revision (`--corpus-revision`, default `4244eaed5`), plus
   `extra-commands.txt`. Each literal also runs after four compound prefixes
   (`cd` lists, an external directory, and the temporary root). The default
   corpus has 55,004 shell inputs.
2. For each revision, the script makes a disposable `git worktree`, copies the
   probe (`probe/AuthorizationCorpusProbe.cs`) and one adapter into
   `Netclaw.Actors.Tests`, builds, and runs the probe. The `gate` adapter reads
   `EvaluateAuthorizationResultAsync` (revisions up to authorization PR 6c).
   The `authorizer` adapter reads `ToolAuthorizer`. `auto` picks the
   production path of the revision.
3. The probe evaluates 21 shell states (Bash: 3 grant states, interactive or
   unattended, Approval or Auto; Bash 5.2: 5 Approval states, see below;
   PowerShell 7: 2 grant states, Approval or Auto) and 24 tool states (3 audiences, interactive or unattended, 4 consent modes)
   with the 62 tool inputs of the differential test. After a consent request,
   it also evaluates the retry with a "Once" answer. After a tool consent
   request, it records a chat grant and evaluates the call again. The probe
   decides the shell inputs in parallel lanes (see "Parallel lanes" below).
4. The script compares the two outputs and writes a report with the outcome
   transitions and the first differences.

Each line holds the outcome, reason, advice, consent request, matched grants,
coverage trace, store lookups, and the analysis that the process may execute.
The probe checks each temporary correction against the current run's managed directory.
It emits the target directory and platform root explicitly, because record text omits internal properties.
The probe replaces run-specific paths with placeholders (`{P}`, `{S}`, `{X}`,
`{R}`, `{T}`, `{REPOSITORY}`, and the GUID of the fake Windows root). It also
replaces the GUID in the `netclaw-approval-matrix-<GUID>` harness folder and
in the `netclaw-testrun-<GUID>` temporary folder of the test process with
`{GUID}`. A `..` path or a basename can show these names. Grant timestamps
compare by presence only.

A `..` path can also reach the folders above the test process temporary root.
These folders hold the revision hash (the first 12 characters), so they differ
for each revision. The probe replaces the revision folder name with
`{REVISION}`, also when a basename shows it alone. The probe fails when the
folder name of `NETCLAW_CORPUS_REPOSITORY` is not a 12-character hash. The
compare step then replaces the paths with fixed tokens before it compares two
lines:

| Text in the line | Token |
|------------------|-------|
| `<out>/worktrees/{REVISION}` (the revision worktree root) | `{REVISION_ROOT}` |
| `<out>/run/{REVISION}` (the parent of the private temporary root) | `{RUN_ROOT}` |
| `<out>` (the work directory) | `{WORK_ROOT}` |

Two revisions with the same behavior therefore give the same lines. The
report shows the tokens, never a revision hash.

Bash 5.2 states:

- The `bash` states use a Bash host with no proved version, so the parser
  state is `Unknown`. A production daemon on Bash 5.2 or 5.3 has the fresh
  no-startup state. Only that state gives literal twins (F1) and the complete
  launch environment (F3).
- The `bash52` states use the Bash 5.2 host: no grant, a grant for anywhere, a
  folder grant in the project, and a chat grant, each interactive, plus an
  unattended folder grant. Use them for a change to twins, assignments, or
  launch facts:

```bash
python3 scripts/authorization-corpus/run.py --base upstream/dev \
  --states bash52-none-i-approval,bash52-anywhere-i-approval,bash52-project-i-approval,bash52-chat-i-approval,bash52-project-u-approval
```

Parallel lanes:

- `--jobs N` sets the number of decision lanes in the probe. The default is
  the CPU count minus 1. `--jobs 1` decides one input at a time, as before.
- The probe uses Akka.Streams: `Source.From(inputs)`, then an ordered
  `SelectAsync(N, decide)`, then one `FileIO.ToFile` sink. The ordered stage
  keeps the input order, and the file sink is the only writer of the output.
- Each lane owns one harness, with its own grant store, store lookup count,
  and folders. A lane decides one input at a time. A shell decision reads only
  its own input and the fixed state of the harness, so the lane count does not
  change a line.
- A tool state stays in one lane. Its chat grants stay in the harness for the
  next inputs, so the input order changes the result.
- The probe fails when a decision fails, when the sink reports an error, when
  the element count is not the input count, or when the sink did not write
  every byte. The script keeps a failed output as `.partial` and never reuses
  it.
- The output for one revision is the same, byte for byte, for each lane
  count. The parallelism is therefore not part of the output name.
- The script prints the line count and the current state every minute.
- On an 8-core machine that also ran other jobs, the probe for one revision
  took 84 minutes with one lane and 25 minutes with 7 lanes. The output was
  byte-identical.

Caution: the probe runs with a private temporary root (`TMPDIR`, `TMP`, and
`TEMP` point into the work directory). The corpus replaces the literal `/tmp`
with that root (`{T}`). A decision therefore never reads the shared `/tmp`,
which other processes change during a run.

Notes:

- The script tests committed revisions only. Commit the work before a run.
- The work directory is `artifacts/authorization-corpus` (`--out` changes it).
  The script reuses an output when the `src/` tree, adapter, probe, corpus,
  and states are the same. A full output (a `src-*.tsv` file) is about
  650 MB. A `--quick` output is about 90 MB.
- The report (`report-*.txt` in the work directory) is the durable output.
  Unless `--keep` is set, the script deletes the head output after the
  compare. It also deletes each worktree with its build output (`bin` and
  `obj`) and a failed probe's `.partial` output. The `.log` file stays.
- The base output is the cache. It stays in the work directory, so the next
  run against the same base skips the build and the probe for the base. A
  self-compare (same `src/` tree on both sides) keeps its one output for the
  same reason. The script prints the number and size of the cached outputs.
  Each new base adds one output, so delete old ones, or run with
  `--prune-cache` to delete every output except the current base output.
- `--fresh-head` probes the head again even when an output exists, and writes
  it to its own file. With the same revision for `--base` and `--head`, zero
  differences shows that two probes of one revision agree.
- Before a full run, check `df -h /` and `uptime`. A full run holds the base
  output (650 MB), the head output (650 MB), and one worktree with its build
  output at a time. Other jobs on the machine slow the build and the probe.
- Each run records its own speed. The script prints a timing block and writes
  it into the report, before the examples. This block comes from a `--quick`
  self-compare on 8 CPUs, with a load average near 90 from other jobs:

```text
timing:
  lanes: 7
  cpus: 8
  load average at the end (1 min): 90.9
  corpus: 0m 10s (10 s)
  build base: 8m 38s (518 s)
  probe base: 23m 16s (1396 s)
  build head: 4m 33s (273 s)
  probe head: 21m 28s (1288 s)
  compare: 0m 45s (45 s)
  total: 59m 04s (3544 s)
```

  A phase that reuses a cached output shows `cache hit (not run)`.
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
| `./scripts/smoke/run-smoke.sh config-retention` | Native retention editor: first key, Backspace, paste, save, and re-entry |
| `./scripts/smoke/run-smoke.sh screenshots` | Screenshot regression: capture + byte-compare against baselines |
| `./scripts/smoke/install-vhs.sh` | Idempotent VHS install (Linux/x86_64 + macOS via Homebrew) |

`run-smoke.sh` publishes the binary (or uses `NETCLAW_SMOKE_CLI` /
`NETCLAW_SMOKE_DAEMON` if exported), installs `vhs`, starts a native
`ollama serve`, and pulls the smoke models automatically.

Local runs with limited disk space can share one binary extraction directory across tape homes.
Set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to an absolute directory that the smoke run owns.
Remove that directory after all smoke processes exit.
For an active run, remove only completed tapes' `.net` directories after confirmation that no process uses those homes.
Retain the tape logs and session data for review.
After a tape completes, confirm that its daemon exits before you remove that tape's downloaded model cache.
Retain its config, logs, and session data.

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
