The stack has three PRs. PR 1 and PR 2 each ship and roll back alone. PR 3 is forward-only. Each PR starts with
failing tests, then the code, then the docs and gates. Each PR keeps its own doc and skill changes. The spec deltas
describe the state after PR 3, and the change syncs after PR 3 (task 10.2). No test uses `Thread.Sleep` or
`Task.Delay`.

# PR 1: channel types, notices, pause, reminder rule, and prompt lifetime

## 1. PR 1 tests first (they fail on `dev`)

- [ ] 1.1 Add channel tests: the sender of each `AgentEvent` equals the owner's run record when a payload names another run; a field 14 row reads as `ChildRunEnded` with sender `ChildRun` and target `Session`. Verify that they fail on `dev`.
- [ ] 1.2 Add a property test: a marker string in a child command never appears in an approval notice text or an event payload. Verify that it fails on `dev`.
- [ ] 1.3 Add notice actor specs: five child dispatches of one owner in one model call give one `child-consent` input (principal `VerifiedAutomation`, sender `child-consent`) at the boundary; a dispatch during compaction gives a notice at the compaction end; an idle session gets one turn for one dispatch; an answer while another request of the owner stays open admits no notice; the last close admits one closing notice; the notice does not join a person's turn. Verify that they fail on `dev`.
- [ ] 1.4 Add per-owner text specs: Alice's notice lists only Alice's runs; with Bob's request still open it says that `spawn_agent` stays paused; the session-clear notice lists the held reminder fires. Verify that they fail on `dev`.
- [ ] 1.5 Add pause actor specs: `spawn_agent` is refused while paused, and the refusal names only the runs that the calling turn may control and the count of the others; no run folder stays; a slash start is accepted; a person input runs a turn and the pause stays; an automated input and a child result notice wait in `Ready` and do not join a running turn; they run after the last close. Verify that they fail on `dev`.
- [ ] 1.6 Add reminder specs: two fires of one reminder while paused leave one pending input (the earlier closed with the existing `InputClosed`, no new field) and run one turn after the pause; the rule holds when a parent prompt is also open; a fire behind only a parent prompt keeps the drop rule; an isolated reminder session never pauses. Verify that they fail on `dev`.
- [ ] 1.7 Add recovery specs: recovery closes every pending `child-consent` input with no turn; if it closed one, it admits one notice with the open set (empty in PR 1, so none); if it closed none, it admits nothing. Verify that they fail on `dev`.
- [ ] 1.8 Add a rollback test: a pending `child-consent` input deserializes and classifies as automation under the principal check alone. Verify that it fails on `dev` (the input kind does not exist).
- [ ] 1.9 Add lifetime tests: a child `ToolInteractionRequest` has `Lifetime = UntilClosed` and a parent prompt has `UntilTurnEnd`; `ChannelOutputEngine` keeps `UntilClosed` prompts at `TurnCompleted`; `ChatViewModel` keeps them at `TurnCompleted` and at `ErrorOutput`; `PendingApprovalPromptTracked` round-trips the optional `lifetime` field, and a row without it reads as `UntilTurnEnd`; a cold-spawned binding keeps a child prompt at turn end. Verify that they fail on `dev`.
- [ ] 1.10 Add a TUI headless typed-key test: a child prompt survives `TurnCompleted` and `ErrorOutput`, and the answer resumes the child (#2523). Verify that it fails on `dev`.

## 2. PR 1 implementation

- [ ] 2.1 Add `AgentAddress`, `AgentEnvelope` (`Sender`, `Target`, `SentAt`), `AgentEvent` with `ChildApprovalRequested`, `ChildApprovalResolved`, `ChildRunEnded`, `ChildApprovalResolution`, `WithdrawReason`, and the `ChildRunResultFile` value object, as closed in-process records. Verify task 1.1 compiles.
- [ ] 2.2 Move `ChildNotice` handling into `AgentEvent.ChildRunEnded` in memory; keep field 14 on disk with a mapper adapter. Verify task 1.1 passes and `ChildNoticeAdmissionSpecs` and `ChildNoticeSerializationTests` pass unchanged.
- [ ] 2.3 Build `ChildApprovalRequested` after `ToolApprovalRequested` persists and `ChildApprovalResolved` in `SessionToolWorkflow` after a resolution or a close. Verify task 1.1 passes.
- [ ] 2.4 Add `AgentEventRenderer` (pure) and the per-owner digest: ready on a new request or on the owner's last close; admitted at once in `Ready`, else at the tool-batch end, turn end, or compaction end before the drain; admitted as `InputAdmitted` with source kind and sender `child-consent`, principal `VerifiedAutomation`, authenticity `LocalProcess`, and the owner's session, audience, boundary, and channel type. Verify tasks 1.2, 1.3, 1.4, and 1.8 pass.
- [ ] 2.5 Close every pending `child-consent` input at recovery and rebuild the digests from the open entries. Verify task 1.7 passes.
- [ ] 2.6 Add the pause predicate to `SessionToolWorkflow`; refuse a `Tool` start in `AcceptChild` with `SpawnPaused` and the per-requester text; hold automated input in `PlanNewInputAbandonment` and stop `DrainBufferedBatch` at it; let `child-consent` inputs pass; run `StartWaitingInputAfterApproval` on the last close. Verify task 1.5 passes.
- [ ] 2.7 Change `TryFindWaitingFire` to replace the earlier fire while the child pause applies, with the existing `InputClosed`. Verify task 1.6 passes.
- [ ] 2.8 Add `ToolInteractionRequest.Lifetime` and the optional `lifetime` proto field on `PendingApprovalPromptTracked`; clear only `UntilTurnEnd` prompts in `ChannelOutputEngine.CompleteTurnAsync` and in `ChatViewModel` at `TurnCompleted` and `ErrorOutput`. Verify tasks 1.9 and 1.10 pass.

## 3. PR 1 docs, skills, smoke, and gates

- [ ] 3.1 Update `docs/architecture/tool-authorization.md` 3.1 (pause as an input placement owner, notice kind and principal, reminder replacement and precedence), 3.5 (data row), 3.6 (agent event channel rules, bridge vs channel table, `Lifetime`, pause rule in place of "Child prompts do not block..."), 4 (automated-input rules of the pause), and 9 (pause gate row). Verify the doc links resolve.
- [ ] 3.2 Update the `agent-coordination` and `netclaw-operations` skills (approval notice, pause, refusal text) and bump `metadata.version`. Add eval cases that inject a `child-consent` notice through the eval harness: the parent does not cancel or respawn a waiting child and tells the operator to answer. Verify `./evals/run-evals.sh` passes.
- [ ] 3.3 Add a native smoke tape for a child prompt that outlives the parent turn in the TUI. Verify `./scripts/smoke/run-smoke.sh light` passes.
- [ ] 3.4 Add the `child-consent` Stryker matrix group with the pause gate (`AcceptChild` and the drain), a script, the expected mutant count, and a `TOOLING.md` row with the estimated cost (about 3 minutes). Verify the script reports all mutants killed and the group stays under the job timeout.
- [ ] 3.5 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify both pass.

# PR 2: withdrawal, redraw, buttons-only child prompts, and the text answer guard

## 4. PR 2 tests first

- [ ] 4.1 Add withdrawal actor specs: a cancel emits one `ToolInteractionClosed` for each open call; an unexpected stop of a live child withdraws with `ChildLost`; a recovery withdrawal uses `DaemonRestarted`; for each `WithdrawReason`, a late click gets the table text with no run ID and no agent name, and nothing runs or is granted. Verify that they fail on PR 1.
- [ ] 4.2 Add a refused-park-after-cancel spec: the run is recorded as `Cancelled`, not lost. Verify that it fails on PR 1.
- [ ] 4.3 Add text answer guard tests on the session and in `PendingApprovalLookup`: two open prompts resolve neither and reply "use the buttons"; one open prompt keeps the current behavior; the earliest is resolved when the sender can answer only that one. Verify that they fail on PR 1.
- [ ] 4.4 Add builder snapshot tests for Slack, Discord, and Mattermost: a child prompt has buttons only, no "reply with" text, and no child identity; the closed redraw of each reason has no child identity. Add a binding test: a failed child prompt post posts no text prompt and the session withdraws with `PromptUnavailable`. Verify that they fail on PR 1.
- [ ] 4.5 Add `ChannelOutputEngine` and TUI tests: `ToolInteractionClosed` finds the prompt by call ID, redraws, removes, and returns `PendingApprovalPromptCleared`; the TUI removes the prompt. Add a headless test: the headless client prints a prompt and its close with no child identity. Verify that they fail on PR 1.

## 5. PR 2 implementation

- [ ] 5.1 Add `ToolInteractionClosed(CallId, Reason)` to the session outputs and the DTO mapper (output only, not persisted). Verify task 4.5 compiles.
- [ ] 5.2 Emit `ToolInteractionClosed` from `CloseChildApprovals`, from recovery, and from a failed child prompt post; add the `Withdrawn` case to `RejectClosedCall` with the reason from the run lifecycle and the call ID prefix; add the five reason texts; record `Cancelled` for a park refused after a cancel. Verify tasks 4.1 and 4.2 pass.
- [ ] 5.3 Apply the text answer guard in `TryResolveTextResponse` and `PendingApprovalLookup.Resolve`. Verify task 4.3 passes.
- [ ] 5.4 Render child prompts with buttons only in the three builders; handle `ToolInteractionClosed` in the engine, the builders, the TUI, and the headless client. Verify tasks 4.4 and 4.5 pass.

## 6. PR 2 docs and gates

- [ ] 6.1 Update `docs/architecture/tool-authorization.md` 3.6 (published contract, withdrawal texts, `RejectClosedCall` list, buttons-only child prompts, text answer guard), 7.5 (honor `Lifetime`, handle `ToolInteractionClosed`), and 9 (text guard gate row). Verify the doc links resolve.
- [ ] 6.2 Add the text answer guard gate to the `child-consent` group with a `TOOLING.md` row (about 3 minutes). Verify all mutants are killed.
- [ ] 6.3 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify both pass.

# PR 3: durable child wait across a restart (#2572)

## 7. PR 3 tests first

- [ ] 7.1 Add serialization round trips for `ChildConversationStarted`, `ChildMessagesRecorded`, `ChildParked`, `ChildRunFinished`, `ChildJournalClosed`, `ChildRunEvent.Parked`, `Resumed`, `JournalPurged`, the `Parked` lifecycle case, `HasChildJournal`, and the parked prompt entries in `SessionSnapshotProto`; assert a manifest and a mapper entry for each. Verify that they fail on PR 2.
- [ ] 7.2 Add persistence ID tests: the form is `subagent-{escaped session ID}/{run ID}`; an ordinal prefix match for `C1/1700` does not match a child of `C1/1700/x`; an escaped ID with `%` matches only by ordinal comparison; the session catalog never lists a child journal; a run that never parks writes no journal. Verify that they fail on PR 2.
- [ ] 7.3 Add park and resume specs (in-memory journal): a child that needs consent persists and stops; the parent records `Parked`; a live answer records `Resumed`, recreates the child, and runs the parked call once; a refused answer runs nothing; a batch with two parked calls waits for both answers; resumed prompt numbers do not repeat. Verify that they fail on PR 2.
- [ ] 7.4 Add the answer-before-park spec (B1): a parked call A and a slow sibling B; the operator approves A while B runs; the owner records `Parked` and `Resumed` together; A runs once; the run is not lost. Verify that it fails on PR 2.
- [ ] 7.5 Add the snapshot spec (B2): park; compaction saves a snapshot and deletes the old journal rows; restart; a click resumes the parked call once. Add a routine-snapshot spec: parked child entries pass `AllowsSnapshot`. Verify that they fail on PR 2.
- [ ] 7.6 Add frozen-facts specs (B3): the resumed child uses the authority of `run.OriginalContext` while another requester's turn runs; it uses the frozen system prompt, tool set, project instructions, operating rules, findings flag, provider, and model; a role fallback records the resolved model; a missing model, provider, or agent definition ends the run as lost with its reason, the notice states it, the parked calls do not run, and no other model is called; a missing frozen tool gives an explicit error result. Verify that they fail on PR 2.
- [ ] 7.7 Add race specs: the park stop does not record a lost child; the resume waits for `Terminated` of the old incarnation; a cancel during resume preparation records `Cancelled` and starts no child; no resume while a restart drain is pending; the purge waits for `Terminated`. Verify that they fail on PR 2.
- [ ] 7.8 Add crash-window specs, one for each row of design D8.9, and the cancel of a parked run. Verify that they fail on PR 2.
- [ ] 7.9 Add purge specs: no purge before the notice is admitted; recovery purges an ended run with `HasChildJournal`; a second purge is harmless; `task.md`, `result.md`, and `logs/session.log` stay. Add a SQLite-backed journal test: after the purge, the only row for the child persistence ID is `ChildJournalClosed`. The in-memory journal cannot prove this. Verify that they fail on PR 2.
- [ ] 7.10 Add recovery and replay specs: a parked request stays open and the session is paused; a non-parked request is withdrawn with `DaemonRestarted`; a new subscriber gets each open child prompt again; Slack, Discord, Mattermost, and the TUI skip a tracked call ID; a session with only parked children passivates and a click wakes it. Verify that they fail on PR 2.

## 8. PR 3 implementation

- [ ] 8.1 Add `SubAgentRunProtocol` with `IWithSubAgentRunId` and `ISubAgentRunEvent : INetclawSerializableMessage`, the five child events, the proto messages, manifests, and mapper entries. Verify task 7.1 passes for the child types.
- [ ] 8.2 Add `ChildRunEvent.Parked(ChildRunPark)`, `Resumed`, `JournalPurged`, the `Parked` lifecycle case, `HasChildJournal`, and the parked prompt entries in `SessionSnapshotProto`; let parked child entries pass `AllowsSnapshot`. Verify tasks 7.1 and 7.5 pass.
- [ ] 8.3 Make `SubAgentActor` a persistent actor with the persistence ID of task 7.2 and an incarnation-unique actor name. Write the journal at the first park only, with `ChildConversationStarted` first. Verify task 7.2 passes.
- [ ] 8.4 Record the resolved provider ID and model ID at start; add an exact provider and model lookup with no role fallback; agree the seam with `subagent-explicit-model-selection`. Verify the model part of task 7.6 passes.
- [ ] 8.5 Change the child bridge to return `ConsentStep.Parked`; at the park point persist the child events, ask the owner, and stop after the ack; the owner releases and unwatches the run, accepts answered prompts, and persists `Parked` (and `Resumed` when all prompts have answers). Verify tasks 7.3 (park part) and 7.4 pass.
- [ ] 8.6 Route every child answer to the durable path; wait for siblings; hold the resume during a restart drain; persist `Resumed`; wait for `Terminated`; prepare off the actor thread with the agent definition, provider, and model checks; discard on cancel; recreate the child with the authority of `run.OriginalContext` and the frozen facts; send `ResumeParkedChild` with the redrive plan; seed the bridge prompt number. Verify tasks 7.3, 7.6, and 7.7 pass.
- [ ] 8.7 Add the end and purge order for runs with `HasChildJournal`: child `ChildRunFinished`; parent `TerminalRecorded`, `result.md`, notice; after `Terminated`, a purge actor (`Recovery.None`) persists `ChildJournalClosed` and calls `DeleteMessages(long.MaxValue)`; then `JournalPurged`. Apply it to lost and cancelled runs. Verify task 7.9 passes.
- [ ] 8.8 Change `RecoveryCompleted` and `RecoverChildren` to the D8.8 rules; replay open child prompts to a new subscriber; skip tracked call IDs in `ChannelOutputEngine` and `ChatViewModel`; let a session with only parked children passivate. Verify tasks 7.8 and 7.10 pass.

## 9. PR 3 docs, gates, and rollout

- [ ] 9.1 Update `docs/architecture/tool-authorization.md` 3.5 (parked entries in the snapshot), 3.6 (every child answer takes the durable path; a parked request survives a restart; the resume uses the run authority and the current policy), 4 (replace the live-only and "expired after restart" text for child prompts), 8 (rehydrate running children as a future scenario), and 9 (resume and purge gate rows). Verify the doc links resolve.
- [ ] 9.2 Update the `netclaw-operations` skill (child journals, purge, frozen model failure) and bump `metadata.version`. Verify `./evals/run-evals.sh` passes.
- [ ] 9.3 Add the resume gate (`Resumed` before any parked call runs) and the purge order gate (no purge before the notice and `Terminated`) to the `child-consent` group with `TOOLING.md` rows (about 3 minutes each). Verify all mutants are killed and the group stays under the job timeout.
- [ ] 9.4 Add a release note and a runbook entry: back up `netclaw.db` before upgrade; the rollout is forward-only. Verify the note is in the PR description and the runbook.
- [ ] 9.5 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify both pass.

## 10. Stack sync

- [ ] 10.1 Before sync, merge the deltas with the open changes that touch the same requirements or seams: `d2-reminders-end-at-admission` (merge the reminder rule: the later fire replaces the earlier one while the child pause applies, and the later fire is dropped elsewhere), `d1-one-authority-per-turn`, `simplify-agent-coordination`, `stop-repeated-tool-cycles` (a resumed child keeps its frozen and loaded tools; cycle state starts empty), `streaming-tool-call-execution` (the park point waits for every call of the response), `close-child-approvals-at-recovery`, `reminder-turn-manages-own-children`, `s2-child-result-notices`, and `subagent-explicit-model-selection`. Verify `openspec validate --strict` passes for each change.
- [ ] 10.2 After PR 3 merges, sync the deltas to the main specs and archive the change. Verify `openspec validate --strict` passes on the main specs.
