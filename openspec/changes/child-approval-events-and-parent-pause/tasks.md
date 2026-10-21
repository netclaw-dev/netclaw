The stack has three PRs. Each PR starts with failing tests, then the code, then the docs and gates. Each PR keeps its
own spec deltas, doc changes, and skill changes. No test uses `Thread.Sleep` or `Task.Delay`.

# PR 1: channel types, approval notices, pause, and reminder rule

## 1. PR 1 tests first (they fail on `dev`)

- [ ] 1.1 Add channel tests: the sender of each `AgentEvent` equals the owner's run record when a payload names another run; a field 14 row reads as `ChildRunEnded` with sender `ChildRun` and target `Session`. Verify that they fail to compile or fail on `dev`.
- [ ] 1.2 Add a property test: a marker string in a child command never appears in an approval notice text or an event payload. Verify that it fails on `dev` (no notice exists).
- [ ] 1.3 Add digest actor specs: five child dispatches in one model call give one `child-consent` input at the boundary; an idle session gets one turn for one dispatch; an answer while another request stays open admits no notice; the last close admits one closing notice. Verify that they fail on `dev`.
- [ ] 1.4 Add pause actor specs: `spawn_agent` is refused while paused (error names the runs, no run folder stays); a slash start is accepted; a person input runs a turn and the pause stays; an automated input and a child result notice wait in `Ready` and do not join a running turn; they run after the last close. Verify that they fail on `dev`.
- [ ] 1.5 Add reminder specs: two fires of one reminder while paused leave one pending input (the earlier closed as superseded) and run one turn after the pause; the closing notice lists the fire; a fire behind a parent consent request keeps the drop rule; an isolated reminder session never pauses. Verify that they fail on `dev`.
- [ ] 1.6 Add a recovery spec: a pending `child-consent` input keeps its automation kind after a restart. Verify that it fails on `dev`.

## 2. PR 1 implementation

- [ ] 2.1 Add `AgentAddress`, `AgentEnvelope` (`Sender`, `Target`, `SentAt`), `AgentEvent` with `ChildApprovalRequested`, `ChildApprovalResolved`, `ChildRunEnded`, and `ChildApprovalResolution`, as closed records with value objects only. Verify task 1.1 compiles.
- [ ] 2.2 Move `ChildNotice` handling into `AgentEvent.ChildRunEnded` in memory; keep field 14 on disk with a mapper adapter. Verify task 1.1 passes and the existing `ChildNoticeAdmissionSpecs` and `ChildNoticeSerializationTests` pass unchanged.
- [ ] 2.3 Build `ChildApprovalRequested` in the dispatch handler after `ToolApprovalRequested` persists, and `ChildApprovalResolved` in `SessionToolWorkflow` after a resolution or a close. Verify task 1.1 passes.
- [ ] 2.4 Add `AgentEventRenderer` (pure) and the actor-local digest with the merge rule (ready on a new request or on the last close; admitted at once in `Ready`, else at the tool-batch end or turn end before the drain). Admit the notice as `InputAdmitted` with source kind `child-consent`. Verify tasks 1.2, 1.3, and 1.6 pass.
- [ ] 2.5 Add the pause predicate to `SessionToolWorkflow`; refuse a `Tool` start in `AcceptChild` with `SpawnPaused`; hold automated input in `PlanNewInputAbandonment` and stop `DrainBufferedBatch` at it; let `child-consent` inputs pass; run `StartWaitingInputAfterApproval` on the last close. Verify task 1.4 passes.
- [ ] 2.6 Change `TryFindWaitingFire` to replace the earlier fire while paused (`InputClosed`, reason `superseded`) and list held fires in the closing notice. Verify task 1.5 passes.
- [ ] 2.7 Add `SubAgentOutput` phases `AwaitingConsent` and `ConsentClosed`, and print the reason in the headless client. Verify a headless test shows "waiting for consent" for an open child request.

## 3. PR 1 docs, skills, and gates

- [ ] 3.1 Update `docs/architecture/tool-authorization.md` 3.1 (pause as an input placement owner, reminder replacement) and 3.6 (agent event channel rules, bridge vs channel table, pause rule in place of "Child prompts do not block..."). Verify the doc links resolve.
- [ ] 3.2 Update the `agent-coordination` and `netclaw-operations` skills (approval notice, pause, refusal text) and bump `metadata.version`. Add eval cases: after an approval notice the parent does not cancel or respawn a waiting child and tells the operator to answer. Verify `./evals/run-evals.sh` passes.
- [ ] 3.3 Add Stryker targets for the owner-stamped sender and for the pause predicate in `AcceptChild` and in the drain, in the `child-owner-and-report` group, with a `TOOLING.md` row. Verify the scripts report all mutants killed.
- [ ] 3.4 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify both pass.

# PR 2: withdrawal, prompt lifetime, redraw, and the text answer guard

## 4. PR 2 tests first

- [ ] 4.1 Add withdrawal actor specs: a cancel emits one `ToolInteractionClosed` for each open call; for each `WithdrawReason`, a late click gets the table text, the text contains no run ID and no agent name, and nothing runs or is granted. Verify that they fail on PR 1.
- [ ] 4.2 Add prompt specs: a child `ToolInteractionRequest` has `Lifetime = UntilClosed` and no child identity; a parent prompt has `UntilTurnEnd`. Verify that they fail on PR 1.
- [ ] 4.3 Add text answer guard tests on the session and in `PendingApprovalLookup`: two open prompts resolve neither and reply "use the buttons"; one open prompt keeps the current behavior; the earliest approval is resolved when the sender can answer only that one. Verify that they fail on PR 1.
- [ ] 4.4 Add `ChannelOutputEngine` tests: `TurnCompleted` keeps `UntilClosed` prompts; `ToolInteractionClosed` redraws, removes, and returns `PendingApprovalPromptCleared`. Add Slack, Discord, and Mattermost builder snapshot tests for the closed redraw of each reason with no child identity. Verify that they fail on PR 1.
- [ ] 4.5 Add a TUI headless typed-key test: a child prompt survives `TurnCompleted`, and the answer resumes the child (#2523). Verify that it fails on PR 1.

## 5. PR 2 implementation

- [ ] 5.1 Add `ToolInteractionRequest.Lifetime` and `ToolInteractionClosed(CallId, Reason)` to the session outputs and the DTO mapper. Verify task 4.2 passes.
- [ ] 5.2 Emit `ToolInteractionClosed` from `CloseChildApprovals` and from recovery; add the `Withdrawn` case to `RejectClosedCall` with the reason from the run lifecycle and the call ID prefix; add the three reason texts. Verify task 4.1 passes.
- [ ] 5.3 Apply the text answer guard in `TryResolveTextResponse` and `PendingApprovalLookup.Resolve`. Verify task 4.3 passes.
- [ ] 5.4 Change `ChannelOutputEngine.CompleteTurnAsync` and `ChatViewModel` to clear only `UntilTurnEnd` prompts; handle `ToolInteractionClosed` in the engine, the three builders, and the TUI. Verify tasks 4.4 and 4.5 pass.

## 6. PR 2 docs, smoke, and gates

- [ ] 6.1 Update `docs/architecture/tool-authorization.md` 3.6 (published contract, withdrawal texts, `RejectClosedCall` list, text answer guard) and 7.5 (honor `Lifetime`, handle `ToolInteractionClosed`). Verify the doc links resolve.
- [ ] 6.2 Add a native smoke tape for a child prompt that outlives the parent turn. Verify `./scripts/smoke/run-smoke.sh light` passes.
- [ ] 6.3 Add a Stryker target for the text answer guard. Verify all mutants are killed.
- [ ] 6.4 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify both pass.

# PR 3: durable child wait across a restart (#2572)

## 7. PR 3 tests first

- [ ] 7.1 Add serialization round trips for `ChildConversationStarted`, `ChildMessagesRecorded`, `ChildParked`, `ChildRunFinished`, `ChildRunSnapshot`, and `ChildRunEvent.Parked`, `Resumed`, `JournalPurged`; assert a manifest and a mapper entry for each. Verify that they fail on PR 2.
- [ ] 7.2 Add persistence ID tests: the form is `subagent-{escaped session ID}/{run ID}`; the prefix of `C1/1700` does not match a child of `C1/1700/x`; the session catalog never lists a child journal. Verify that they fail on PR 2.
- [ ] 7.3 Add park and resume specs (in-memory journal): a child that needs consent persists and stops; the parent records `Parked`; a live answer records `Resumed`, recreates the child, and runs the parked call once; a refused answer runs nothing; a batch with two parked calls waits for both answers; resumed prompt numbers do not repeat; the resumed child uses the frozen system prompt and model. Verify that they fail on PR 2.
- [ ] 7.4 Add a frozen-model spec: with the model removed, the resume ends the run as lost with `frozen_model_unavailable`, the notice states it, the parked call does not run, and no other model is called. Verify that it fails on PR 2.
- [ ] 7.5 Add crash-window specs, one for each row of design D8.7, and the cancel of a parked run. Verify that they fail on PR 2.
- [ ] 7.6 Add purge specs: no purge before the notice is admitted; the purge removes messages and snapshots and records `JournalPurged`; recovery purges a reported run with no `JournalPurged`; a second purge is harmless; `task.md`, `result.md`, and `logs/session.log` stay. Verify that they fail on PR 2.
- [ ] 7.7 Add recovery specs: a parked request stays open and the session is paused; a non-parked request is withdrawn with `DaemonRestarted`; a pending `child-consent` input with open requests resumes; a new subscriber gets each open child prompt again; a binding skips a tracked call ID; a session with only parked children passivates and a click wakes it. Verify that they fail on PR 2.

## 8. PR 3 implementation

- [ ] 8.1 Add `SubAgentRunProtocol` with `IWithSubAgentRunId` and `ISubAgentRunEvent : INetclawSerializableMessage`, the four child events, the snapshot, the proto messages, manifests, and mapper entries. Verify task 7.1 passes for the child types.
- [ ] 8.2 Add `ChildRunEvent.Parked(ChildRunPark)`, `Resumed`, `JournalPurged`, the `Parked` lifecycle case, and the snapshot mapping. Verify task 7.1 passes.
- [ ] 8.3 Make `SubAgentActor` a persistent actor with the persistence ID of task 7.2. Persist `ChildConversationStarted` (frozen prompt and model reference) before the first model call. Verify task 7.2 passes.
- [ ] 8.4 Add an exact model lookup to the chat client provider with no role fallback; agree the seam with `subagent-explicit-model-selection`. Verify task 7.4 passes.
- [ ] 8.5 Change the child bridge to return `ConsentStep.Parked`; at the park point persist `ChildMessagesRecorded` and `ChildParked`, ask the owner, and stop after the ack; the owner checks the open entries and persists `Parked`. Verify the park part of task 7.3 passes.
- [ ] 8.6 Route every answer for a child prompt to the durable path; wait for siblings; persist `Resumed`; recreate the child (replay); send `ResumeParkedChild` with the redrive plan; seed the bridge prompt number. Verify task 7.3 passes.
- [ ] 8.7 Add the end and purge order: child `ChildRunFinished`; parent `TerminalRecorded`, `result.md`, notice; then the purge actor and `JournalPurged`. Apply it to lost and cancelled runs. Verify task 7.6 passes.
- [ ] 8.8 Change `RecoveryCompleted` and `RecoverChildren` to the D8.6 rules; replay open child prompts to a new subscriber; skip tracked call IDs in `ChannelOutputEngine`; let a session with only parked children passivate. Verify tasks 7.5 and 7.7 pass.

## 9. PR 3 docs, gates, and rollout

- [ ] 9.1 Update `docs/architecture/tool-authorization.md` 3.6 (a parked request survives a restart; every child answer takes the durable path; the gate uses the current policy) and 8 (rehydrate running children as a future scenario). Verify the doc links resolve.
- [ ] 9.2 Update the `netclaw-operations` skill (child journals, purge, frozen model failure) and bump `metadata.version`. Verify `./evals/run-evals.sh` passes.
- [ ] 9.3 Add Stryker targets for the resume gate (`Resumed` before any parked call runs) and the purge order (no purge before the notice). Verify all mutants are killed.
- [ ] 9.4 Add a release note: back up `netclaw.db` before upgrade; the rollout is forward-only. Verify the note is in the PR description and the runbook.
- [ ] 9.5 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify both pass.

## 10. Stack sync

- [ ] 10.1 Before sync, merge the deltas with the open changes that touch the same requirements (`close-child-approvals-at-recovery`, `reminder-turn-manages-own-children`, `s2-child-result-notices`, `subagent-explicit-model-selection`). Verify `openspec validate --strict` passes for each change.
