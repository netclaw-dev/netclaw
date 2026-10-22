## Context

See `proposal.md` (Why). Terms follow [the engineering glossary](../../../docs/spec/GLOSSARY.md): consent request,
one-time consent, requester, open child run, child consent pause, parked child run. This section states the current
mechanics on `upstream/dev` (1d67c023c). Line numbers are for that commit.

### Current mechanics

1. **The child waits in memory.** `SubAgentActor.ExecuteToolsAsync` catches `ToolApprovalRequiredException` and awaits
   `IParentConsentBridge.RequestConsentAsync` inside the tool task (`SubAgentActor.cs:1640-1714`). The batch ends at
   `Task.WhenAll` (`:1754`). The watchdog stops while a wait is open (`SubAgentApprovalWaitStarted`, `:744`).
2. **The bridge says why a restart withdraws.** `ParentSessionApprovalBridge.RequestConsentAsync` (`:117-127`): "a
   sub-agent cannot resume after passivation". The bridge numbers child prompts in memory with `Interlocked`
   (`:178-190`). For a child prompt it sets `OriginalChildCallId` and leaves `ManagedTemporaryDirectory` empty
   (`:168-172`).
3. **The request is journaled.** `SessionToolWorkflow.ChildApprovalCapability` sets `PersistApprovalState = true`,
   `SourceChildRunId`, and `ChildContext` (`SessionToolWorkflow.Children.cs:171-187`).
   `HandleToolInteractionRequestDispatch` persists `ToolApprovalRequested` (`LlmSessionActor.cs:3408-3442`). The event
   holds no display text and no messy flag.
4. **The answer needs the live wait.** A child answer goes to `HandleProcessingApprovalResponseAsync`, also when the
   session is idle (`LlmSessionActor.cs:3778-3783`). `PrepareLiveResponse` must claim the live wait. With no wait it
   rejects with `no_live_wait` (`SessionToolWorkflow.Approvals.cs:220-242`).
5. **The child state is in memory only.** `SubAgentActor` holds:
   - `_history` (`:108`): the system prompt, the task message, the assistant tool calls, the tool results, and the
     nudges, as `Microsoft.Extensions.AI` messages;
   - the run scope (`ToolRunScope`): project directory, inherited working directory, recent files, model input
     modalities, delivery targets (`SubAgentSpawner.PrepareRunAsync`, `:262-300`);
   - the loaded deferred tools, the file activity, and the managed temporary correction state;
   - the loop guard in `TurnStateTracker`: tool iteration and call counts, up to six completed action and outcome
     hashes, the recurrence episodes, and the refused-repeat count;
   - the spawn facts: the agent prompt with the skill overlay, the model role, the findings flag, and three budgets.
6. **Batch completion has effects.** The `ToolExecutionCompleted` handler (`SubAgentActor.cs:565-640`) applies, for
   each result: file activity, `TryApplyProjectDirectory` (a `set_working_directory` receipt rebuilds the scope and the
   system prompt), a tool exposure request, `load_tool` activation, managed temporary updates, the cycle outcome
   categories, the media nudge, and the repeat nudge.
7. **Checkpoints must grow.** `SessionState.Apply(ChildRunEvent.Checkpointed)` throws when the round is not above the
   recorded round (`SessionState.ChildRuns.cs:76-79`). The owner applies it in the command handler
   (`LlmSessionActor.Children.cs:72-84`), so a bad round crashes the session actor.
8. **The run record is durable.** `BackgroundChildRun` (`BackgroundChildRun.cs:22-42`) holds the run ID, agent,
   `OriginalContext`, the initial working snapshot, and the lifecycle. The session snapshot holds every run record
   (`SessionSnapshotV2Proto.child_runs`).
9. **The parent already re-drives its own parked batch.** After an answer, also after a restart or passivation, the
   session re-drives only the parked calls with an `ApprovalRedrivePlan` (`SessionToolWorkflow.cs:378-437`). The
   pipeline applies one plan entry to a call context in `SessionToolExecutionPipeline.cs:538-575`: it seeds the
   one-time consent, or it returns the refusal text with the managed temporary denial hint.
10. **Recovery withdraws.** `RecoveryCompleted` withdraws every child entry that replay left open
    (`LlmSessionActor.cs:265`). `RecoveredWithdrawReason` throws for an unknown lifecycle
    (`SessionToolWorkflow.Children.cs:104`). `RecoverChildren` records each `Accepted` or `Cancelling` run as lost
    (`LlmSessionActor.Children.cs:451-460`), and `LostChildResult` says "External effects are uncertain".
11. **Restart drain.** In `Passivating`, a drain nacks every answer with the restart message
    (`LlmSessionActor.cs:1234-1239`). `UnreportedRuns()` counts every run that is not reported, and it decides the
    restart reminder (`LlmSessionActor.cs:1343`, `4326`, `4380`).
12. **Snapshots.** `SaveSnapshotIfSafe` skips the routine snapshot while a durable input is pending or an approval entry
    is open (`LlmSessionActor.cs:3504-3515`). Held reminder fires are pending inputs. The compaction snapshot has no
    such check (`LlmSessionActor.cs:1034`).
13. **Passivation.** A live child keeps the session in memory (`HasUnreportedChildren`, `LlmSessionActor.cs:1782`).

### Constraints

- One owner for each decision. The session actor owns the run record and every decision about it.
- Reuse before you add. Anchor on the run record, the session journal, and the parent re-drive code.
- No second persistence system. No new persistence ID.
- Default deny. No silent fallback. A resume that cannot meet a spawn rule fails loudly.
- Persisted types are proto messages with a manifest and a mapper entry.
- Tests are deterministic, with no sleep.

### Owner decisions

The owner confirmed these decisions on 22 October 2026. They change decisions of 21 October 2026.

| ID | Decision |
| --- | --- |
| P7 | Replace the child journal with the park in the run record (D2). |
| P10 | Resolve the model by role at resume. Log the recorded model ID and the new model ID. |
| B1 | Show a prompt only after its park is recorded. |
| Profile | A deleted agent profile fails the resume with the existing reason `spawn_unavailable` (PR 1 cut plan). |
| M10 | The park holds the loop-guard state, so a park does not reset it. |
| M13 | The session database (`netclaw.db`) holds child transcripts, in the same data class as the parent transcript. |

### Cuts (22 October 2026)

The owner asked for fewer parts and no second systems. An audit proposed cuts. The table lists each cut, its audit
row, and who decided it.

| Cut | Audit row | Decided by |
| --- | --- | --- |
| Prompt replay at a join, the `Replayed` flag, the call ID skip in `ChannelOutputEngine`, the permanent-error lists of each binding, `MattermostPostFailureClassifier`, the Discord classifier, Slack `is_archived`, `display_text` and `is_messy`, and the TUI and headless PR. Rules M3, M4, and N2 are cut by the owner, 22 Oct. | 14-17 | owner |
| The tool fingerprint (B3, M7, N3, N12) | fingerprint rows | owner |
| PR 1b and the feature branch; the merge PR | split | owner |
| The Ask for `ChildParkAccepted` and `ChildParkRefused`; the child reuses the terminal handshake | 2 | owner (audit, safe) |
| `_parksInFlight`; one self message `ChildEventRejected`. Its test stays as task 3.9, `A_rejected_two_request_park_records_once`, because it proves N7 with no extra code. | 3-5 | owner (audit, safe) |
| Most of `PreBatchScope`; only two directories stay | 9 | owner (audit, safe) |
| A second copy of batch completion; the stored receipt facts | 10 | owner (audit, safe) |
| `ManagedTemporaryRetryTargetProto` | 11 | owner (audit, safe) |
| Most spawn facts; only the skill overlay and the model ID stay | 12 | owner (audit, safe) |
| Wrapper proto messages | 19 | owner (audit, safe) |
| The replay-time apply and the snapshot hydrate; one recovery path | 22 | owner (audit, safe) |
| The two-event cancel write | 23 | owner (audit, safe) |
| The mapper span of `child-park-recovery` | 34 | owner (audit, safe) |
| `ResumeSubAgent` | 35 | owner (audit, safe) |

**Cost of the replay cut, stated plainly:**

- Slack, Mattermost, and Discord restore their tracked `UntilClosed` prompts from the binding journal, so a prompt
  that a binding posted stays live after a restart.
- A TUI that starts again after a restart does not show an open child prompt. A parent prompt has the same behavior
  today.
- A crash between the `Parked` write and the prompt post leaves the prompt hidden until someone answers or cancels.
  The parent notice and `check_agent_run` still show the open request, so the operator can cancel the run.
- A future replay is one separate change for parent and child prompts together.

### PR 1 cut plan (22 October 2026)

The owner approved a cut plan for PR 1. Where this design and the cut plan differ, the cut plan wins.

- M6 is cut. A resumed child uses `run.OriginalContext` as it is, as the first spawn does.
- Cut: `RepeatNudge`, `BatchObservations` and the cycle-batch rebuild, `LoadedTools`, `ModelId`, the typed reason
  `park_not_recorded`, and the `parked` state of `check_agent_run`. Each refusal reuses an existing reason.
- Deferred to a later PR: `DeferredMedia`, `FileActivity`, and the managed temporary retry target
  (`platform_temporary_root` on `ApprovalRedriveEntry`).
- Deferred to PR 2: the 4 MiB cap, and the update of the park in the persist callback of an answer.
- In PR 1, a park drops the media nudge, the repeat nudge, and the loop observation of its batch.
- A resumed child does not keep the deferred tools that the parked incarnation loaded. It can load them again.

**Cost of the fingerprint cut:** this matches the parent re-drive, which checks no fingerprint. The answer stays bound
by `OneTimeConsent.Covers(toolName, keys)` and the exact command. Binding every grant and consent to the MCP endpoint,
for parent and child alike, is the general endpoint-binding issue.

## Goals / Non-Goals

**Goals:**

- A child that parked at consent survives a restart, a config reload, a compaction snapshot, and passivation.
- One resume path for a live answer and for an answer after a restart.
- The resumed call runs at most once, under the recorded authority, and it passes every gate again.
- The resumed child continues with the same loop guard, the same working directories, and the same batch.

**Non-Goals:**

- The resume of a running child that did not park. Recovery records it as lost, as today.
- A frozen model or a frozen tool set. A tool fingerprint.
- Prompt replay to a new subscriber.
- A change to the notice, pause, withdrawal, or text answer rules of `child-approval-events-and-parent-pause`.

## Decisions

### D1. Park always

A child call that needs consent does not wait. The child records a parked marker for the call and lets the other calls
of the batch finish. When `Task.WhenAll` ends and one or more calls parked, the child builds its park, sets its
completion flag, stops the watchdog, and uses the existing terminal handshake (audit row 2): it Tells the owner
`ChildParkReached(run, park)` and calls `Become` to wait for `ChildTerminalCommitted`, as `Complete` does
(`SubAgentActor.cs:1095-1097`). The owner sends `ChildTerminalCommitted` when it accepts the park or after it records a
terminal for the run. The child then stops. `PostStop` sends no `ActorStopped` result, because the completion flag is
set.

Why: one resume path. The change removes the live child wait, the watchdog pause for approval waits,
`_pendingApprovalWaits`, and the child use of `ApprovalChannel`.

Trade-off: the prompt appears when the batch ends, not while a slow sibling runs.

### D2. The park

The park is one record, `ChildRunPark`. PR 1 keeps it actor-local in the owner. PR 2 makes it durable as
`ChildRunEvent.Parked` in the run record.

**Batch effects (audit row 10).** The existing `ToolExecutionCompleted` handler splits into two steps: "apply the
result effects" and "continue the loop". At the park, the child runs "apply" for the calls that finished: it appends
their result messages, applies file activity, `TryApplyProjectDirectory`, exposure requests, and `load_tool`. The park
then records the child state that results. Only the parked calls wait for the resume. The park drops the two nudges
(media and repeat) and the loop observation of the batch (PR 1 cut plan).

One order differs from a live batch. Live, the effects apply in call order after the whole batch. With a park, the
effects of the finished calls apply at the park, and the effects of the parked calls apply after they run. So a parked
`set_working_directory` call wins over a finished `set_working_directory` call of the same batch, whatever the call
order. No user message comes between two tool results, because the nudges still come after all results.

| Field | Content | Why |
| --- | --- | --- |
| `Transcript` | every message after the system message: the assistant message of the open batch and the results of its finished calls, as `SerializableChatMessage` | it exists only in the child |
| `DeferredMedia` (deferred to a later PR) | the model-input media references of the finished calls | the media nudge comes after all results |
| `ScopeDirectories` | the project directory and the inherited working directory (audit row 9) | the parked calls run in the directories of the live batch (H3) |
| `FileActivity` (deferred to a later PR) | the `WorkingContextDelta` of the child after the finished calls: project directory, read files, changed files | the existing type; the round is in `LoopGuard` only |
| `LoopGuard` | `TurnLoopGuardState` from PR 0 (`TurnStateTracker.ExportLoopGuard`) | a park does not reset the loop guard (M10) |
| `SkillOverlay` | the skill overlay of the spawn, or none (audit row 12) | the profile does not hold it |
| `Requests` | one `ToolApprovalRequested` per parked call, and later its `ToolApprovalResolved` | the facts that the gate made |

Rules:

- **The round is stored once.** `LoopGuard.ToolIterationCount` is the only copy of the round in the park. The park
  holds no checkpoint. When `Resumed` applies (PR 2), the reducer moves the run to `Accepted` with a checkpoint that it
  builds from the park: the round from `LoopGuard.ToolIterationCount`. The resume
  seeds `_checkpointRound` from the same count, so its first checkpoint is round N+1 (H1, test 3.5).
- **The rest comes from the run record and the profile.** The requester, boundary, channel, delivery targets, and
  initial recent files come from `run.OriginalContext` and `run.InitialWorkingSnapshot`. The model input modalities
  come from the model that resolves at resume. The model role, the findings flag, the budgets, and the base agent
  prompt come from the current agent profile.
- **Managed temporary retry (audit row 11; deferred to a later PR).** A parked request keeps the existing `managed_temporary_directory`
  (field 21 of `ToolApprovalRequestedProto`): it is the retry directory and the denial directory. One new string,
  `platform_temporary_root`, completes the retry target. The managed temporary correction state is not in the park:
  the parent never persists it, and `ClearApprovalTurn` clears it at each turn end.
- **The child keeps its own serializable transcript.** It holds a `List<SerializableChatMessage>` next to `_history`
  and converts each assistant message once, when it arrives (M12). Reasoning content is not kept, as in the parent
  history.
- **The owner numbers the prompts.** The counter `PromptsIssued` is on the run record (actor-local in PR 1).
- **Size (PR 2).** The owner logs the size of each park in bytes. A park above `ChildRunPark.MaxBytes` (4 MiB, a constant)
  ends the run with `park_too_large`, with no prompt, and no parked call runs (M8).
- **Data class.** Child transcripts go into `netclaw.db`, in the same data class as the parent transcript (M13).

### D3. Answers and the resume gate

```text
schematic: answer for a child prompt p of run r (live or after recovery; same handler)
  if p is not open: RejectClosedCall (existing texts)
  ValidateResponse(p, response)                      # requester binding, option (unchanged)
  record a session or persistent grant if the option asks (unchanged)
  Persist ToolApprovalResolved(p)
    callback: apply it to r.Park.Requests in the session state, and to _toolWorkflow
    emit ToolInteractionClosed(Answered); digest; pause check   (unchanged)
    TryResumeParkedRun(r)

TryResumeParkedRun(r):
  if r has no park: return
  if any request of r.Park is open: return           # guard 1: a batch with two parked calls waits for both
  if a restart drain is pending: return              # guard 2: nothing ran. PR 2: recovery resumes.
                                                     #   PR 1: the restart records the run as lost.
  plan := SubAgentSpawner.PrepareResume(r, park)     # D4; no side effects
  if plan failed: RecordChildTerminal(r, Failed(reason), "The parked calls did not run.")
  else Persist ChildRunEvent.Resumed(r)              # PR 2; PR 1 removes the actor-local park instead
    callback: ActorOf(plan.Props, "subagent-{agent}-{runId}-{PromptsIssued}"); Watch; TrackChild;
              Tell RunSubAgent { Resume = (park, answers) }
```

- The answer path needs no live wait. `PrepareLiveResponse` and its claim stay for parent calls only.
- **The live answer updates the park (PR 2).** Today the live path updates only `_toolWorkflow` (`LlmSessionActor.cs:3754`).
  Without the state update, a compaction snapshot after the answer would hold the park with an open request.
- `Resumed` persists before any parked call runs. A crash after `Resumed` is uncertain work: recovery records the run
  as lost and never runs the call again.
- The preparation runs on the actor thread with no await. It reads two files: the operating rules and the project
  instructions. A session with no spawner ends the run with `spawn_unavailable`.
- The actor name has the prompt counter, so a new incarnation never collides with an old actor that still stops.
- **Answer during a restart drain.** In `Ready` or `Processing` with a drain requested, the session records the
  answer and does not resume. In `Passivating`, the session nacks the answer with the restart message, as today.
- **Start message (audit row 35).** No new message type. `RunSubAgent` gets one nullable field, `Resume`, with the
  park and the per-call answers. A null field means a fresh start. A fresh start and a resume are two real runtime
  states, so the field is nullable by design. On a resume the child builds `_history` from the park.

**Refusals and journal failure (H4, N7, N9; audit rows 2-5).** Each refusal reuses an existing reason (PR 1 cut plan).
The owner records
the terminal through `RecordChildTerminal`, which already unwatches the child, sends `ChildTerminalCommitted`, and
ignores a second call for an ended run.

| Case | Owner action | Outcome |
| --- | --- | --- |
| The run is cancelling | `RecordChildTerminal(Cancelled, cancelled_by_parent)` | the run ends cancelled; no prompt |
| The sender is not the owned child | send the existing stop signal `ChildTerminalCommitted` to the sender | no record; the sender stops itself |
| The park is above the cap (PR 2) | `RecordChildTerminal(Failed, park_too_large)` | no prompt; no parked call runs |
| The journal rejects the park | `OnPersistRejected` sends `Self.Tell(ChildEventRejected(runId))`; the handler calls `RecordChildTerminal(Failed, tool_execution_failed)` | the notice says "The child could not wait for consent. The calls that needed consent did not run." |
| The journal rejects `Resumed` (PR 2) | `Self.Tell(ChildEventRejected(runId, resume_not_recorded))` | the run ends `Failed`, "The parked calls did not run."; no actor starts |
| The journal fails a write | the session actor stops (Akka.Persistence) | recovery decides from the journal |

**Order in `OnPersistRejected`.** The child-event branch is the first branch of `OnPersistRejected`. It sends
`ChildEventRejected`, calls `base.OnPersistRejected`, and returns. It runs before the existing check that sends
`PersistRejectedTurnFailure` (`LlmSessionActor.cs:2727-2738`). Without this order, a rejected child event would fail
the parent's turn. The branch has two triggers:

- PR 1: a `ToolApprovalRequested` with `SourceChildRunId` (the park writes its requests);
- PR 2: `ChildRunEvent.Parked` or `ChildRunEvent.Resumed`.

A rejected write of several events calls `OnPersistRejected` once for each event. The first `ChildEventRejected`
records the terminal. Each later one finds the run ended, and `RecordChildTerminal` does nothing. So the owner records
once with no extra state.

### D4. A resume is a spawn from a recorded conversation

The rule: **the conversation, the batch, and the directories are recorded; the environment resolves again; the
authority comes from the run record.**

| Fact | Source at resume |
| --- | --- |
| Requester, boundary, channel, delivery targets | `run.OriginalContext`. Never the current turn, never the current parent. |
| Project directory, inherited working directory | `park.ScopeDirectories` (H3) |
| Recent files | `run.InitialWorkingSnapshot` (`park.FileActivity` is deferred to a later PR) |
| Model input modalities | the model that resolves at resume |
| Base agent prompt, model role, findings flag, budgets | the current agent profile; a deleted profile fails with `spawn_unavailable` |
| Skill overlay | `park.SkillOverlay` |
| Conversation, loop guard | the park |
| Model client | `IChatClientProvider.GetClient(role)` now |
| Tool set | `SubAgentSpawner.ResolveTools` now, under the current registry and the recorded audience |
| System message | `BuildSystemPrompt` now |

`SubAgentSpawner.PrepareResume(run, park)` shares the scope and props builder with `PrepareRunAsync`. It applies the
spawn rules again: a spawner exists; `SubAgentConfig.Enabled` and a non-`Public` audience (`spawn_unavailable`); the
profile exists (`spawn_unavailable`); at least one child tool (`no_tools_available`); `file_read`
(`no_result_reader`); the provider gives a client (`spawn_error`). PR 2 adds one more rule for a park that comes from
the journal: the loop guard is valid (no negative count, `RefusedRepeatCount` at most `EscalatedRefusedRepeat + 1`,
`EqualRounds` from 0 to 2), or the run ends `Failed` with `park_damaged`. `PrepareResume` runs from a command, never
inside recovery, so a bad park never stops recovery. The mapper only maps; it decides nothing.

The resumed child:

1. Seeds `_history` with the new system message and the transcript, and its serializable list with the transcript.
2. Restores the loop guard with `TurnStateTracker.RestoreLoopGuard` (PR 0). `_checkpointRound` comes from the same
   `ToolIterationCount`.
3. Canonicalizes the parked calls and runs only those calls through `ExecuteToolsAsync`, in `park.ScopeDirectories`.
   The shared helper of PR 0 applies one plan entry to a call context (`ApprovalRedriveEntry`, M11):
   - approved: seed the one-time consent from `OneTimeApprovalKeys.Create(patterns, candidates, cwd)` of the journaled
     request, and restore the authorization attempt ID. The managed temporary retry target is deferred to a later PR;
   - refused: no dispatch; the refusal text of `ConsentRefusalText.For`.
4. Runs "apply the result effects" for the parked calls, so a parked `set_working_directory` wins. Then it runs
   "continue the loop".

**Requester check.** PR 0 (`edf9e9a26`) has `ApprovalRequestBuilder` with two entry points, `ForSessionCall` and
`ForChildCall`. `ForChildCall` already runs the requester check (`EnsureAuthorityContext`). PR 1 builds each parked
child prompt with `ForChildCall`, so the owner never builds a parked child prompt without a requester binding.

### D5. Recovery keeps parked runs (PR 2)

```text
schematic: RecoveryCompleted, changed lines only (one path for the journal and the snapshot; audit row 22)
  replay and snapshot only rebuild SessionState: Parked, Resumed, and ToolApprovalResolved update r.Park
  split each pending input with SplitByAuthority once; every later step takes the parsed authorities
  for each run r in _state.ChildRuns with a park: put r.Park.Requests into _toolWorkflow (open or resolved)
  withdraw only the child entries whose run has no park   # RecoveredWithdrawReason never sees Parked
  _restartWithdrawals := those closes only
  CloseRecoveredConsentNotices()                       # unchanged; the open parked requests rebuild the digest
  if the pause holds after recovery: RestoreWaitingInputs(held inputs) now   # M5
  log child_parks_recovered count=.. bytes=..          # only when count > 0
  RecoverChildren():
    Accepted or Cancelling        -> lost (unchanged text)
    Parked, a request open        -> nothing; the run waits with no actor
    Parked, all answered          -> TryResumeParkedRun (D3); nothing ran
  ResumeOrphanNotices; StartHeldInputs only if the pause ended at recovery
```

- **One recovery path.** Replay and the snapshot offer only rebuild `SessionState`. `RecoveryCompleted` builds the
  open entries from `_state.ChildRuns` for both cases. There is no replay-time apply and no snapshot hydrate.
- **Parsed authorities.** A throw inside a recovery handler goes to `OnRecoveryFailure` and stops the session. Every
  recovery step takes the parsed authorities of one `SplitByAuthority` call. No step parses a record again.
- **A damaged park (PR 0 note).** Recovery does not call `RestoreLoopGuard`; only the resumed child does. The loop
  guard check is a spawn rule of `PrepareResume` (D4), which runs from a command. A bad park ends the run `Failed`
  with `park_damaged`. `RestoreLoopGuard` keeps its throw only as a programming-error check.
- `RecoverChildren` and `LostChildResult` get `Parked` cases. A lost parked run never says "uncertain": "The child
  waited for consent and did not resume. The parked calls did not run."
- `HasUnreportedChildren`: a parked run with an open request does not count, so the session can passivate. A parked
  run with all answers counts until it resumes. In PR 1 every actor-local park counts.
- **The pause comes back by itself.** `IsChildConsentPaused` reads the open child entries.
- **Held input (M5, N8).** When the pause holds after recovery, `RecoveryCompleted` puts the held inputs in the waiting
  queue before the stashed commands run, so a stashed fire of the same reminder replaces the held fire. Damaged inputs
  stay pending with a log line.
- **One pause predicate (PR 0).** `ChildConsentPause.Holds(TurnContext authority, bool isChildNotice)` decides which
  input the pause holds, for the drain, the recovery, and the restart resume.
- **A restart reminder respects the pause (N1).** While `IsChildConsentPaused` is true, `TryResumeInterruptedInput`
  leaves out every input that the pause holds. When no input that the pause does not hold remains, it acknowledges
  with no turn. This rule edits the same method as #2587 (PR #2592), so PR 2 comes after PR #2592.
- **Restart reminder (M2).** `UnreportedRuns()` does not count a parked run with an open request. A parked run with all
  answers counts.
- **Snapshots and the journal delete.** After `SaveSnapshotSuccess`, the session deletes the journal rows up to the
  snapshot (`LlmSessionActor.cs:2649`). The snapshot is then the only copy of a park. Recovery rebuilds the park from
  the snapshot. The next snapshot after `Resumed` no longer holds the park, so the delete after it removes the old
  `Parked` rows. The guard against a silent loss is the exhaustive round-trip test of the snapshot mapper.
- **Snapshots (M8).** `AllowsSnapshot` ignores child entries, because the run record holds them. The pause still
  blocks the routine snapshot while it holds a pending input (`LlmSessionActor.cs:3509`). The cap bounds each park.
- **Recovery cost checks.** A recovery-time test on the SQLite journal compares a session with several parks at the
  cap against a plain session with the same parent history. A stopwatch measures each recovery up to the reply of a
  probe message that the session stashes during recovery, so there is no sleep. The bound comes from measured numbers
  on the CI runner, with a wide margin. At `RecoveryCompleted`, the session writes one Info line,
  `child_parks_recovered count={count} bytes={bytes}`, only when the count is above 0. The line holds no content.

### D6. The prompt in each channel

| Surface | After a restart |
| --- | --- |
| Slack, Mattermost, Discord | The binding journal restores the tracked `UntilClosed` prompt. A click carries the same call ID. PR 2 stops the withdrawal of a parked prompt at recovery. |
| TUI that stays open | `ChatViewModel` keeps `UntilClosed` prompts across a reconnect. |
| TUI that starts again | No prompt. A parent prompt has the same behavior today. |
| Headless | No change. The exit rule stays with #2541. |
| Mattermost with no callback URL | A child prompt is refused (buttons only), as today. |

A first post that fails keeps the existing rule: the session withdraws the request with `PromptUnavailable`.

### D7. Cancel and end of a parked run

- **Cancel (audit row 23).** `ControlChild` accepts a parked run and calls `RecordChildTerminal` with a `Cancelled`
  result and the reason `cancelled_by_parent`. That writes one `TerminalRecorded`. `RecordChildTerminal` gets a
  `Parked` arm, and `SessionState.Apply` gets the `(Parked, TerminalRecorded)` case. One helper maps a terminal to its
  withdraw reason (`cancelled_by_parent` gives `CancelledByParent`, lost gives `ChildLost`, any other end gives
  `ChildEnded`). `WithdrawChildApprovals` and `RecoveredWithdrawReason` both call it, so the live redraw and the
  recovered late click say "cancelled" for a cancelled run.
- **Check.** PR 1 cuts the `check_agent_run` status `state: "parked"`.
  The tool keeps its existing lifecycle status and `pending_approval_count`.
- **End.** A resumed run ends as any run ends.

### Formats (PR 2, forward-only)

| Message | Change | Field numbers |
| --- | --- | --- |
| `ChildRunEventV2Proto` | `fact` oneof: `ChildRunParkProto parked`, `bool resumed` | 8, 9 |
| `BackgroundChildRunV2Proto` | `lifecycle` oneof: `ChildRunParkProto parked`; field 20 is not in the oneof | 21 |
| `BackgroundChildRunV2Proto` | `int32 prompts_issued` | 22 |
| `ToolApprovalRequestedProto` | `optional string platform_temporary_root` | 25 |
| new | `ChildRunParkProto` (the park; reuses `SerializableChatMessageProto`, `SerializableMediaReferenceProto`, `ToolLoopObservationProto`, `ChildWorkingContextProto`, and `ChildRunApprovalProto` for the requests) and `ChildLoopGuardProto` (the loop guard) | new messages, each with a manifest and a mapper entry |

Two new messages in total, and no one-field wrapper (audit row 19). No snapshot field is new. No persistence ID is new.

**Downgrade, release-note text:** "This release stores a child run that waits for consent in the session journal. An
older Netclaw binary cannot recover a session that holds such a child. Before you install an older binary, answer or
cancel each waiting child, or back up `netclaw.db` and restore the backup with the older binary."

### Ordered flow

```mermaid
sequenceDiagram
    participant C1 as SubAgentActor (incarnation 1)
    participant S as LlmSessionActor (owner)
    participant J as Session journal and snapshot
    participant Ch as Channel binding
    participant Op as Operator
    participant C2 as SubAgentActor (incarnation 2)
    C1->>C1: consent needed -> parked marker; siblings finish; apply their effects
    C1->>S: Tell ChildParkReached(run, park); Become(wait for ChildTerminalCommitted)
    S->>S: owner, Accepted, and size checks
    S->>J: Persist ChildRunEvent.Parked (transcript, deferred parts, requests)
    S->>S: unwatch and release C1; open entries; pause; digest
    S-->>C1: ChildTerminalCommitted, then C1 stops
    S->>Ch: ToolInteractionRequest (UntilClosed)
    Note over S,J: restart, config reload, compaction, or passivation
    J->>S: recovery rebuilds the state; RecoveryCompleted opens the parked requests; pause back
    Op->>Ch: click "approve once" (binding restored the tracked prompt)
    Ch->>S: ToolInteractionResponse (same call ID)
    S->>S: ValidateResponse (requester, option)
    S->>J: Persist ToolApprovalResolved (applied to the park)
    S->>S: last open request -> PrepareResume (spawn rules)
    S->>J: Persist ChildRunEvent.Resumed
    S->>C2: ActorOf; RunSubAgent { Resume }
    C2->>C2: gate checks the parked call again; one-time consent; call runs once
    C2->>C2: apply its effects; nudges; continue the loop; checkpoint N+1
```

### Failure modes

| Case | Outcome | Loud signal |
| --- | --- | --- |
| The journal rejects the park | The run ends `Failed`, `tool_execution_failed`. No prompt. No parked call runs. | result notice |
| The journal fails a write | The session stops. Recovery decides from the journal. | session stop (existing) |
| The park is above the cap (PR 2) | The run ends `Failed`, `park_too_large`. | result notice and error log with the size |
| Crash before `Parked` | Recovery records the run as lost. No prompt was shown. | lost notice |
| Crash after `Parked`, before the prompt post | The request is open, and no prompt shows. The parent notice and `check_agent_run` show it. The operator can cancel. | notice |
| Crash after the last answer, before `Resumed` | Recovery resumes. Nothing ran. | resume log line |
| The journal rejects `Resumed` | The run ends `Failed`, `resume_not_recorded`. | result notice |
| Crash or second restart after `Resumed` | Recovery records the run as lost. The call does not run again. | lost notice |
| A damaged loop guard in a park | At resume, `PrepareResume` ends the run `Failed`, `park_damaged`. Recovery is not involved. | result notice and error log |
| Answer during a drain, `Ready` or `Processing` | The answer is recorded. Recovery resumes. | resume log line |
| Answer during a drain, `Passivating` | Nack with the restart message. The prompt stays open. | nack (existing) |
| Answer after `Resumed` | "already has an answer". Nothing runs. | nack `approval_already_answered` |
| The policy now denies the parked call | The gate denies it. | denial result and audit log |
| The policy now needs another consent | The child parks again. | new prompt |
| The parked call's tool is gone | "Unknown tool: {name}". The child continues. | warning log |
| The role maps to another model | The resume uses the current model of the role. | info log with both model IDs |
| No usable provider, deleted profile, subagents disabled, no tools, no `file_read`, no spawner | The run ends `Failed` with the reason. No parked call runs. | result notice |
| A restart reminder while the pause holds | Held input stays in the queue. | `restart_resume` log line |

### Security boundary

The resumed call is a new dispatch, so it passes the whole gate again. Three facts bound it:

1. **Authority.** The requester, boundary, channel, and delivery targets come from `run.OriginalContext`. The working
   directories come from the park. None comes from the current turn or the current parent.
2. **Policy.** The tool set and the gate use the current registry and `ToolAccessPolicy`. A deny rule or a protected
   path after the park wins over the answer.
3. **Consent.** The only consent is the journaled answer of the run requester, validated by `ValidateResponse`. It is
   a one-time consent that `OneTimeConsent.Covers(toolName, keys)` binds to the exact tool name and candidate keys of
   that prompt and that child call ID, used once. `Resumed` persists before the call runs.

Positive example: Alice's child parks on `gh pr merge 42 --squash`. An operator adds an MCP server, and the daemon
restarts. Alice clicks "Approve once" on the old Slack prompt. The session persists the answer and `Resumed`, starts the
child under Alice's recorded authority, and the command runs once in the recorded working directory.

Negative examples:

- After the park, the operator adds `gh pr merge` to the shell deny list and reloads. Alice approves. The gate denies
  the call. Nothing runs.
- Bob clicks Alice's button after the restart. `ValidateResponse` reads the requester from the run record and rejects
  Bob (`approval_wrong_requester`). Nothing is recorded and nothing runs.
- After the park, the parent session changes its project directory. The resumed call still runs in the recorded
  directory.
- After the resume, the child model calls `gh pr merge 43 --squash`. The one-time consent covers only call 42. The
  child parks again.
- The daemon crashes after `Resumed` and before the call result. Recovery records the run as lost. The call does not
  run a second time.

Limit: an MCP server key that the operator points at a new endpoint with the same tool name and the same command keys
gets the old answer, as a parent re-drive does today. The general endpoint-binding issue covers this for parent and
child alike.

### Owners and data

| Decision | Owner | Data |
| --- | --- | --- |
| Park a batch, apply finished effects, build the park | `SubAgentActor` | actor-local until sent |
| Accept a park, number the prompts, size cap, refusals | `LlmSessionActor` (Children partial) | PR 1 actor-local; PR 2 durable `ChildRunEvent.Parked` |
| Prompt shape and requester check | `ApprovalRequestBuilder.ForChildCall` (PR 0) | call-local |
| Answer validity | `SessionToolWorkflow.ValidateResponse` (unchanged) | durable `ToolApprovalResolved`, also in the park |
| Resume gate | `LlmSessionActor.TryResumeParkedRun` | PR 2 durable `ChildRunEvent.Resumed` |
| Resume preparation, spawn rules | `SubAgentSpawner.PrepareResume` | call-local |
| Apply one plan entry to a call context | `ApprovalRedriveEntry` (PR 0) | call-local |
| Batch completion after the resume | `SubAgentActor` (incarnation 2) | actor-local from the park |
| Open entries, pause | `SessionToolWorkflow` | actor-local, built at `RecoveryCompleted` from the run records |
| Recovery choice for each run | `LlmSessionActor.RecoverChildren` | durable lifecycle |

### Alternatives rejected

| Alternative | Why rejected |
| --- | --- |
| Keep withdrawing (today) | It breaks the steady state that the owner chose. |
| Child journal (D8, P7) | A second persistence system. The run record is durable already. |
| Park only at a graceful drain | Two resume paths. A crash loses the child. |
| Live wait plus a park record | Two resume paths, and the answer-before-park race comes back. |
| Freeze the model and the tool set, or a tool fingerprint | Stored schemas and a second binding rule that the parent re-drive does not have. Endpoint binding belongs to the general endpoint-binding issue. |
| Prompt replay for child prompts only | A second delivery path for one prompt kind. A future replay covers parent and child prompts together. |
| Run each child as a full `LlmSessionActor` | Much larger than this issue. |

### Stack and size

Three PRs, all to `dev`. No feature branch and no merge PR.

| PR | Base | Content | Format | Size (code plus tests) |
| --- | --- | --- | --- | --- |
| PR 0 | `dev` | Pure refactors with no behavior change: `ApprovalRequestBuilder` (`ForSessionCall`, `ForChildCall`, with the requester check), `ApprovalRedriveEntry`, the loop-guard export and restore, and `ChildConsentPause.Holds`. Branch `refactor/durable-child-pr0`. | none | measured on the branch: about 960 changed lines (861 added, 102 removed), most of them tests |
| PR 1 | `dev`, after PR 0 | Park and resume in memory, per the PR 1 cut plan: the park (transcript, loop guard, scope directories, skill overlay, requests), the checkpoint seed, the resume through `PrepareResume` under `run.OriginalContext`, the resume gate, the answer path with no live wait, one refusal path with existing reasons, the `OnPersistRejected` order, the owned-sender check, cancel of a parked run, the drain guard, the prompt counter in the actor name, the passivation rule, the parked prompt built with `ApprovalRequestBuilder.ForChildCall`, and the `docs/architecture/tool-authorization.md` update. The existing restart rules apply. PR 1 alone gives no restart value; it ships with PR 2. | none | measured on the branch (see the PR) |
| PR 2 | `dev`, after PR 1 and PR #2592 (#2587) | Durable `Parked` and `Resumed`, the two proto messages, the one recovery path, the damaged-park spawn rule, passivation, restart-reminder eligibility and the pause rule (N1), held input at recovery, the size cap and log, the recovery cost checks, the snapshot round-trip test, and the downgrade note. | forward-only | 1,050 to 1,550 lines |

PR 1 ships alone. A restart in PR 1 follows the existing rules: the run is `Accepted` in the journal, recovery records
it as lost, and the prompt is withdrawn with `DaemonRestarted`.

### Tests and Stryker

- The first tests compile against stub types, so they fail on assertions, not on compile errors.
- `child-resume-gate` (PR 1, `child-consent` group): the two pure functions of `ChildParkRules` (PR 1 cut plan).
  `AcceptPark` accepts a park only from the owned child of an `Accepted` run. `ResumeGate` waits for every answer and
  for the end of a restart drain. The tables of `ChildParkRulesTests` kill each mutant.
- `child-park-recovery` (PR 2, same group, about 3 minutes): the decision in `RecoverChildren` for each lifecycle and
  the child-entry rule of `AllowsSnapshot`. The mapper span is cut (audit row 34). The exhaustive round-trip test is
  the guard of the snapshot mapper.
- The security tests, not Stryker, guard the authority source and the order "persist `Resumed`, then spawn".

## Risks / Trade-offs

- [The session journal and snapshot hold the child conversation while the run is parked.] → Only parked runs write it.
  `Resumed` drops it from the state. The size log and the cap bound each park.
- [The pause blocks the routine snapshot while it holds a pending input.] → The cap bounds each park event.
- [A restarted TUI shows no open child prompt, and a crash before the post hides the prompt.] → Owner choice (replay
  cut). The parent notice and `check_agent_run` show the open request.
- [An MCP key that points at a new endpoint keeps the old answer.] → Same as the parent re-drive. The general
  endpoint-binding issue.
- [A parked `set_working_directory` wins over a finished one of the same batch.] → Stated in D2. The model sees the
  results of both.
- [The resumed child gets a new system message and the current profile, and reasoning content is not kept.] → The
  parent session has the same behavior after a restart.
- [Forward-only format from PR 2.] → See the downgrade text.
- [A running child is still lost at a restart.] → Out of scope.
- [Conflict with `child-approval-events-and-parent-pause`.] → Its D8, its PR 3 tasks, and its park and purge deltas
  must leave that change before it syncs (task 1.2).

## Migration Plan

1. Merge this spec PR.
2. Remove the D8 material from `child-approval-events-and-parent-pause` with the OpenSpec skills, then sync and archive
   that change.
3. PR 0 into `dev`. No behavior change.
4. PR 1 into `dev`. No format change. Rollback with the binary.
5. After PR #2592 merges: PR 2 into `dev` (forward-only). Publish the downgrade text. Back up `netclaw.db`.
6. Sync this change and archive it.
