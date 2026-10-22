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

### Owner decisions (confirmed by the owner, 22 Oct 2026)

These decisions change decisions of 21 October 2026. The owner confirmed each one on 22 October 2026.

| ID | Decision |
| --- | --- |
| P7 | Replace the child journal with the park in the run record (D2). |
| P10 | Resolve the model by role at resume. Log the recorded model and the new model. |
| B3 | Resolve the tools under the current policy. Check a fingerprint (server ID and schema hash) for each parked call. On a mismatch, park the call again with a new prompt. |
| B1 | Show a prompt only after its park is recorded. |
| Profile | A deleted agent profile fails the resume with `agent_definition_unavailable`. |
| M6 | Derive the trust of the recorded requester and channel again at resume. Use the narrower audience. |
| M10 | The park holds the small loop-guard state, so a park does not reset it. |
| M13 | The session database (`netclaw.db`) holds child transcripts, in the same data class as the parent transcript. |

## Goals / Non-Goals

**Goals:**

- A child that parked at consent survives a restart, a config reload, a compaction snapshot, and passivation.
- One resume path for a live answer and for an answer after a restart.
- The resumed call runs at most once, under the recorded authority and the narrower current trust, and it passes every
  gate again.
- The resumed child continues as if it had never stopped: same loop guard, same batch effects, same path facts.

**Non-Goals:**

- The resume of a running child that did not park. Recovery records it as lost, as today.
- A frozen model or a frozen tool set.
- A change to the notice, pause, withdrawal, or text answer rules of `child-approval-events-and-parent-pause`.

## Decisions

### D1. Park always

A child call that needs consent does not wait. The child records a parked marker for the call and lets the other calls
of the batch finish. When `Task.WhenAll` ends and one or more calls parked, the child builds its park, asks the owner
to accept it, and stops after the acknowledgement. At the park the child sets its completion flag and stops the
watchdog, so `PostStop` sends no `ActorStopped` result and no timer fires.

Why: one resume path. A live answer and an answer after a restart start the same recreation. The change removes the
live child wait, the watchdog pause for approval waits, `_pendingApprovalWaits`, and the child use of
`ApprovalChannel`.

Trade-off: the prompt appears when the batch ends, not while a slow sibling runs.

### D2. The park

The park is one record, `ChildRunPark`. PR 1a and PR 1b keep it actor-local in the owner. PR 2 makes it durable as
`ChildRunEvent.Parked` in the run record. The content is the same in both PRs.

| Field | Content | Why |
| --- | --- | --- |
| `Transcript` | every message after the system message, up to and with the assistant message of the open batch, as `SerializableChatMessage` | it exists only in the child |
| `OpenBatch.Settled` | for each call that did not park: its result message and its receipt facts (file activity, declared project directory, exposure request tool name, managed temporary update, cycle outcome category, result hash, pending-job flag, synthetic flag, authorization attempt ID), plus its model-input media references | these calls ran and never run again; their effects apply at batch completion (H2) |
| `OpenBatch.RepeatNudge` | the repeat nudge of the batch, or none | it comes after all results |
| `PreBatchScope` | project directory, inherited working directory, recent files, model input modalities, default and requested delivery targets | the parked calls run with the scope that the live batch had (H3) |
| `Checkpoint` | `ChildRunCheckpoint` before the batch: round, summary, file activity | the existing checkpoint type |
| `LoopGuard` | tool iteration and call counts, up to six completed action and outcome hashes, the last blocked action, the recurrence episodes, the refused-repeat count | a park does not reset the loop guard (M10); the checkpoint round stays monotonic (H1) |
| `LoadedTools` | the names of the deferred tools that the child loaded | `_loadedDeferredToolNames` |
| `ManagedTemporary` | the managed temporary correction state before the batch | the corrections continue (M1) |
| `Spawn` | agent name, agent prompt with the skill overlay, model role, findings flag, three budgets, the resolved model ID (for the log) | the run record does not have them |
| `Requests` | one `ToolApprovalRequested` per parked call. Each has the display text, the messy flag, the managed temporary retry target, the denial directory, and the tool fingerprint. Later its `ToolApprovalResolved` joins it. | the facts that the gate made, and what the resume needs to check and to seed consent |

Rules:

- **The child keeps its own serializable transcript.** It holds a `List<SerializableChatMessage>` next to `_history`,
  as the parent holds `SessionState.History`. It converts each assistant message once, when it arrives, with the
  converter that the parent uses. It never rebuilds the transcript from `_history` with `FromAiMessage`, because that
  writes media files again (`ChatMessageConverter.cs:251`) (M12). Reasoning content is not kept, as in the parent
  history (L7).
- **The open batch follows the parent rule.** No user message comes between two tool results, because a provider
  refuses that history. The nudges come after all results.
- **The owner numbers the prompts.** The counter `PromptsIssued` is on the run record (actor-local in PR 1a and PR 1b). The prompt
  call ID keeps its form, `{runId}/subagent-approval/{n}`. One shared prompt builder (now inside
  `ParentSessionApprovalBridge.PromptAsync`) makes the request for the parent and for the child.
- **Size.** The owner logs the size of each park in bytes. A park above a hard cap (the constant `ChildRunPark.MaxBytes`,
  4 MiB; no configuration knob) is refused: the run ends `Failed` with `park_too_large`, no prompt
  appears, and no parked call runs (M8).
- **Data class.** Child transcripts go into `netclaw.db`, in the same data class as the parent transcript (M13). No
  tool reads them. The parent model never sees them.

Proto reuse (PR 2): `SerializableChatMessageProto`, `ChildRunCheckpointProto`, `SerializableMediaReferenceProto`,
`ToolLoopObservationProto`, and the existing `ChildRunApprovalProto { request, resolution }`.

### D3. Answers and the resume gate

```text
schematic: answer for a child prompt p of run r (live or after recovery; same handler)
  if p is not open: RejectClosedCall (existing texts)
  ValidateResponse(p, response)                      # requester binding, option (unchanged)
  record a session or persistent grant if the option asks (unchanged)
  Persist ToolApprovalResolved(p)                    # PR 2: also applied to r.Park.Requests
    emit ToolInteractionClosed(Answered); digest; pause check   (unchanged)
    TryResumeParkedRun(r)

TryResumeParkedRun(r):
  if r has no park: return
  if any request of r.Park is open: return           # guard 1: a batch with two parked calls waits for both
  if a restart drain is pending: return              # guard 2: recovery resumes; nothing ran
  plan := SubAgentSpawner.PrepareResume(r, park)     # D4; no side effects
  if plan failed: RecordChildTerminal(r, Failed(reason), "The parked calls did not run.")
  else Persist ChildRunEvent.Resumed(r)              # PR 2; PR 1a removes the actor-local park instead
    on success: ActorOf(plan.Props, "subagent-{agent}-{runId}-{PromptsIssued}"); Watch; TrackChild; Tell ResumeSubAgent
    on rejection or failure: RecordChildTerminal(r, Failed(resume_not_recorded), "The parked calls did not run.")
```

- The answer path needs no live wait. `PrepareLiveResponse` and its claim stay for parent calls only.
- `Resumed` persists before any parked call runs. A crash after `Resumed` is uncertain work: recovery records the run
  as lost and never runs the call again.
- The preparation runs on the actor thread with no await, so a cancel cannot come between the preparation and
  `Resumed`. It reads two files from disk: the operating rules and the project instructions, through
  `ISystemPromptProvider`. The session reads the same files on its thread at recovery (`SetSystemPrompt`) (L4).
- A session with no spawner (`_subAgentSpawner` is null) cannot resume. The run ends `Failed` with
  `spawn_unavailable` and "No child spawner is configured." (L5).
- The actor name has the prompt counter, so a new incarnation never collides with an old actor that still stops.
- **Answer during a restart drain (L1).** In `Ready` or `Processing` with a drain requested, the session records the
  answer and does not resume. Recovery then resumes the park. In `Passivating` with a drain requested, the session
  nacks the answer with the restart message, as it does today. The prompt stays open, and the operator clicks again
  after the restart.

**Journal failure (H4).**

| Write | On rejection or failure | Outcome |
| --- | --- | --- |
| Accept a park (PR 1a: the requests; PR 2: `Parked`) | the owner replies `Status.Failure` to the child | the child completes `Failed` with `park_not_recorded`; no prompt appears; the result notice says "The child could not wait for consent. The calls that needed consent did not run." |
| `Resumed` (PR 2) | `OnPersistRejected` or `OnPersistFailure` | the run ends `Failed` with `resume_not_recorded` and "The parked calls did not run."; no actor starts. A persist failure stops the session as today; recovery finds the park with all answers and resumes it. |

### D4. A resume is a spawn from a recorded conversation

The rule: **the spawn arguments and the path facts are recorded; the environment resolves again; the authority comes
from the run record, narrowed by the current trust.**

| Fact | Source at resume |
| --- | --- |
| Requester, boundary, channel, delivery targets | `run.OriginalContext` and `park.PreBatchScope`. Never the current turn, never the current parent. |
| Audience | the narrower of the recorded audience and the audience that the session trust deriver gives now for the recorded requester and channel (M6) |
| Project directory, inherited working directory, recent files, modalities | `park.PreBatchScope` (H3) |
| Agent prompt, overlay, model role, findings flag, budgets | `park.Spawn`. The agent profile must still exist in the registry, or the resume fails with `agent_definition_unavailable`. |
| Conversation, open batch, loop guard, loaded tools, managed temporary state | the park |
| Model client | `IChatClientProvider.GetClient(park.Spawn.ModelRole)` now, as a spawn does. The session logs the recorded model ID and the new model ID. |
| Tool set | `SubAgentSpawner.ResolveTools` now, under the current registry and the resume audience |
| System message | `BuildSystemPrompt` now, with the operating rules and project instructions read again |
| Consent capability | `SessionToolWorkflow.ChildApprovalCapability(run)` now |

`SubAgentSpawner.PrepareResume(run, park)` shares the scope and props builder with `PrepareRunAsync`. It applies the
spawn rules again:

| Rule | Reason when it fails |
| --- | --- |
| a spawner exists | `spawn_unavailable` |
| `SubAgentConfig.Enabled`, and the resume audience is not `Public` | `spawn_unavailable` (existing) |
| the agent profile exists | `agent_definition_unavailable` (new) |
| at least one child tool under the current policy | `no_tools_available` (existing) |
| `file_read` is available to the requester | `no_result_reader` (existing) |
| the provider is not degraded (`IChatClientProvider.IsDegraded`) | `model_unavailable` (new) |

The resumed child:

1. Seeds `_history` with the new system message and the transcript, and seeds its serializable transcript.
2. Restores the loop guard. `TurnStateTracker` gets an export and a restore method. `ToolIterationCount` and
   `_checkpointRound` start at `park.Checkpoint.CompletedRound`, so its first checkpoint is round N+1 (H1).
3. Activates each name in `park.LoadedTools` that `TryActivateDiscoveredTool` still accepts.
4. Checks the fingerprint of each parked call: the tool name, the MCP server ID for an MCP tool, and the hash of the
   current schema. On a mismatch the call does not run. The child parks that call again, and the session shows a new
   prompt. The old answer does not apply (B3, M7).
5. Canonicalizes the parked calls as `HandleToolCalls` does, and runs only those calls through `ExecuteToolsAsync`,
   with the pre-batch scope. One helper applies one plan entry to a call context. The parent pipeline
   (`SessionToolExecutionPipeline.cs:538-575`) and the child both call it (M11):
   - approved: seed the one-time consent from `OneTimeApprovalKeys.Create(patterns, candidates, cwd)` of the journaled
     request, restore the authorization attempt ID, and mark the managed temporary retry target (M1);
   - refused: no dispatch; the refusal text of `ConsentRefusalText.For`, with the managed temporary denial hint when
     the request has a denial directory (M1).
6. Completes the batch in the live order (H2): the result messages in call order, then for each result in call order
   its receipt effects (file activity, project directory, exposure, `load_tool`, managed temporary update, cycle
   outcome), then the media nudge, then the repeat nudge. A sibling `set_working_directory` therefore applies after the
   parked calls ran, as it would live.
7. Continues the model loop. Its next checkpoint goes to the owner as usual.

Why resolve the model and the tools again: the parent session does the same after a restart or a config reload. The
current policy decides, which is the default-deny direction. The fingerprint check stops an answer from applying to a
tool that changed under the same name.

### D5. Recovery keeps parked runs (PR 2)

```text
schematic: RecoveryCompleted, changed lines only
  replay: Parked applies its requests to SessionToolWorkflow; ToolApprovalResolved resolves them
  snapshot offer: HydrateParkedChildApprovals()       # pattern of HydrateLegacyChildApprovals
  withdraw only the child entries whose run is not Parked   # RecoveredWithdrawReason never sees Parked
  _restartWithdrawals := those closes only
  CloseRecoveredConsentNotices()                      # unchanged; the open parked requests rebuild the digest
  pausedAtRecovery := an open child request of a run that is Accepted or Parked
  if the pause holds after recovery: RestoreWaitingInputs(held inputs) now, inside RecoveryCompleted   # M5
  RecoverChildren():
    Accepted or Cancelling        -> lost (unchanged text)
    Parked, a request open        -> nothing; the run waits with no actor
    Parked, all answered          -> TryResumeParkedRun (D3); nothing ran
  ResumeOrphanNotices; StartHeldInputs only if the pause ended at recovery (unchanged code)
  replay each open parked prompt to new subscribers (D6)
```

Changes to existing code (H5):

- `RecoveredWithdrawReason` gets no `Parked` case. Its caller skips parked runs, so the throw stays a defect guard.
- `RecoverChildren` gets the two `Parked` cases above.
- `LostChildResult` gets a `Parked` case for a park that cannot resume: "The child waited for consent and did not
  resume. The parked calls did not run." It never says "uncertain" for a parked run.
- `HasUnreportedChildren`: a parked run with an open request does not count, so the session can passivate. A parked
  run with all answers counts until it resumes.
- In PR 1a and PR 1b the park is actor-local, so `HasUnreportedChildren` counts every actor-local park, and the session does not
  passivate. A restart before PR 2 follows the existing rules: the run is `Accepted` in the journal, recovery records it
  as lost, and the prompt is withdrawn with `DaemonRestarted`.

Other rules:

- **The pause comes back by itself.** `IsChildConsentPaused` reads the open child entries. Hydration puts the parked
  requests back.
- **Held input (M5).** When the pause holds after recovery, `RecoveryCompleted` puts the held inputs in the waiting
  queue before Akka.Persistence unstashes the commands that came during recovery. A stashed fire of the same reminder
  then finds the waiting fire, and the replace rule closes the older fire. One reminder runs once. When the pause ended
  at recovery (only withdrawn requests), the existing `StartHeldInputs` path stays.
- **Restart reminder (M2).** `UnreportedRuns()` does not count a parked run with an open request: no turn can run for
  it, and a click wakes the session. A parked run with all answers counts, because recovery must resume it.
- **Snapshots and the journal delete.** After `SaveSnapshotSuccess`, the session deletes the journal rows up to the
  snapshot (`LlmSessionActor.cs:2649`). The snapshot is then the only copy of a park. Recovery rebuilds the park from
  the snapshot. The snapshot holds the park in the run record: the `parked` lifecycle case (field 21 of
  `BackgroundChildRunV2Proto`) carries the whole park, and its `requests` carry each request with its answer. The next
  snapshot after `Resumed` no longer holds the park, so the delete after that snapshot removes the old `Parked` rows.
  A mapper that stops writing one park field would lose that field with no error, so two guards protect the mapper:
  an exhaustive round-trip test (task 8.10) and the mapper span of the Stryker target `child-park-recovery`.
- **Snapshots (M8).** `AllowsSnapshot` ignores child entries, because the run record holds them. The pause still
  blocks the routine snapshot when it holds a pending input, because `SaveSnapshotIfSafe` skips while any durable
  input is pending (`LlmSessionActor.cs:3509`). Then the journal keeps every park event until the pause ends or a
  compaction snapshot runs. The size log and the cap of D2 bound this.
- **Passivation.** A click wakes the session, and the answer handler resumes the child.
- **Prerequisite.** #2587 must merge before PR 2. A restart resume must queue every pending input, so the held input
  of a paused session does not stay with no turn.

### D6. The prompt stays live in each channel

| Surface | After a restart | Change |
| --- | --- | --- |
| Slack, Mattermost, Discord | The binding journal restores the tracked prompt (`PendingApprovalPromptTracked` with `UntilClosed`). A click carries the same call ID. | PR 2: recovery does not withdraw a parked prompt. The session replays the open parked prompts at a join, and `ChannelOutputEngine` skips a tracked call ID. |
| TUI that stays open | `ChatViewModel` keeps `UntilClosed` prompts across a reconnect. | None. |
| TUI that starts again | The view model is empty. | PR 3: `ChatViewModel` skips a call ID that it holds; typed-key test; smoke run. |
| Headless | It prints a prompt and its close. It cannot answer. | PR 3: it prints a replayed prompt once (`_shownPrompts`). The exit rule stays with #2541. |
| Mattermost with no callback URL | A child prompt is refused (buttons only). | None. |

- **Replay (M3).** PR 2 sends each open parked prompt to a new subscriber at its join. A crash between the `Parked`
  write and the prompt output therefore still shows the prompt, once a channel binding joins the recovered session.
  Limit: open parked runs do not get a restart reminder (M2), so after such a crash the binding joins at the next
  message or click in the thread.
- **A replayed prompt that fails to post (M4).** The replayed `ToolInteractionRequest` carries `Replayed = true`. A
  binding that cannot post a replayed prompt logs a warning and does not send `prompt_unavailable`
  (`SlackThreadBindingActor.cs:1092-1110` and the Mattermost and Discord equivalents). The request stays open. A first
  post that fails keeps the existing rule: the session withdraws it with `PromptUnavailable`.
- The replay rebuilds `ToolInteractionRequest` from the parked request, with the new `display_text` and `is_messy`
  fields. Both fields land in PR 2 (L3). The labels come from the option keys (`ApprovalOptionKeys.LabelFor`).

### D7. Cancel and end of a parked run

- **Cancel.** `ControlChild` accepts a parked run. No actor must acknowledge. The owner writes `CancellationRequested`
  and `TerminalRecorded(Cancelled, cancelled_by_parent)` in one `PersistAll`. It withdraws the open prompts with
  `CancelledByParent`, writes `result.md`, and admits the result notice.
- **Park refused.** The owner accepts a park only for an `Accepted` run that it owns. Otherwise it replies
  `Status.Failure`, and the child completes as cancelled through its own token (existing path).
- **Check.** `check_agent_run` shows `state: "parked"` and the open request count.
- **End.** A resumed run ends as any run ends. `Resumed` drops the park from the state. The journal rows go at the next
  snapshot, as every session event does.

### Formats (PR 2, forward-only)

| Message | Change | Field numbers (L6) |
| --- | --- | --- |
| `ChildRunEventV2Proto` | `fact` oneof: `ChildRunParkProto parked`, `bool resumed` | 8, 9 |
| `BackgroundChildRunV2Proto` | `lifecycle` oneof: `ChildRunParkedStateV2Proto parked`; field 20 (`source_reminder_id`) is not in the oneof, so the oneof skips it | 21 |
| `BackgroundChildRunV2Proto` | `int32 prompts_issued` | 22 |
| `ToolApprovalRequestedProto` | `optional string display_text`, `optional bool is_messy`, `optional string managed_temporary_retry_target`, `optional string tool_fingerprint` | 25, 26, 27, 28 |
| `ChildRunParkedStateV2Proto` | `ChildRunParkProto park`; `ChildRunParkProto` has `repeated ChildRunApprovalProto requests` (request and answer) and `int32` and message fields for the other park fields | 1 (park); field numbers of `ChildRunParkProto` are set in PR 2 |
| new | `ChildRunParkProto`, `ChildRunParkedStateV2Proto`, `ChildParkSettledResultProto`, `ChildParkScopeProto`, `ChildLoopGuardProto`, `ChildSpawnFactsProto` | new messages, each with a manifest and a mapper entry |

The existing `managed_temporary_directory` (21) holds the denial directory. No snapshot field is new. No persistence ID
is new.

**Downgrade (M9), release-note text:** "This release stores a child run that waits for consent in the session
journal. An older Netclaw binary cannot recover a session that holds such a child. Before you install an older binary,
answer or cancel each waiting child, or back up `netclaw.db` and restore the backup with the older binary."

### Ordered flow

```mermaid
sequenceDiagram
    participant C1 as SubAgentActor (incarnation 1)
    participant S as LlmSessionActor (owner)
    participant J as Session journal and snapshot
    participant Ch as Channel binding
    participant Op as Operator
    participant C2 as SubAgentActor (incarnation 2)
    C1->>C1: gate: consent needed -> parked marker; siblings finish (Task.WhenAll)
    C1->>S: Ask ChildParkReached(run, park)
    S->>S: size check; owner and Accepted check
    S->>J: Persist ChildRunEvent.Parked (transcript, open batch, requests)
    S->>S: unwatch and release C1; open entries; pause; digest
    S-->>C1: ack, then C1 stops (completion flag set; no ActorStopped result)
    S->>Ch: ToolInteractionRequest (UntilClosed)
    Note over S,J: restart, config reload, compaction, or passivation
    J->>S: recovery: Parked run; requests open; pause back; held input queued
    S->>Ch: replay open parked prompt at join (binding skips a tracked call ID)
    Op->>Ch: click "approve once"
    Ch->>S: ToolInteractionResponse (same call ID)
    S->>S: ValidateResponse (requester, option)
    S->>J: Persist ToolApprovalResolved
    S->>S: last open request of the park -> PrepareResume (spawn rules, narrower audience)
    S->>J: Persist ChildRunEvent.Resumed
    S->>C2: ActorOf; ResumeSubAgent(plan)
    C2->>C2: fingerprint check; gate checks the parked call again; one-time consent; call runs once
    C2->>C2: complete the batch in live order; restore loop guard; checkpoint N+1
```

### Failure modes

| Case | Outcome | Loud signal |
| --- | --- | --- |
| The journal rejects or fails the park | The run ends `Failed`, `park_not_recorded`. No prompt. No parked call runs. | result notice |
| The park is above the cap | The run ends `Failed`, `park_too_large`. No prompt. No parked call runs. | result notice and error log with the size |
| Crash before the park is recorded | The run is `Accepted`, so recovery records it as lost. No prompt was shown. | lost notice (existing) |
| Crash after `Parked`, before the prompt output | The run waits. The replay at the next join shows the prompt. | recovery log line |
| Crash after the last answer, before `Resumed` | Recovery resumes. Nothing ran. | resume log line |
| The journal rejects or fails `Resumed` | The run ends `Failed`, `resume_not_recorded`. No actor. A persist failure stops the session, and recovery resumes. | result notice or session stop |
| Crash or second restart after `Resumed` | Recovery records the run as lost. The call does not run again. | lost notice |
| Answer during a drain, `Ready` or `Processing` | The answer is recorded. Recovery resumes. | resume log line |
| Answer during a drain, `Passivating` | Nack with the restart message. The prompt stays open. | nack (existing) |
| Answer after `Resumed` | "already has an answer". Nothing runs. | nack `approval_already_answered` |
| The policy now denies the parked call | The gate denies it. The child gets the access-denied result. | denial result and audit log |
| The policy now needs another consent | The one-time consent does not cover it. The child parks again. | new prompt |
| The tool's fingerprint changed | The call does not run. The child parks it again. | new prompt and warning log |
| The parked call's tool is gone | "Unknown tool: {name}". The child continues. | warning log (existing) |
| The trust of the requester is narrower now | The child runs with the narrower audience. A `Public` audience fails the spawn rule. | `spawn_unavailable` or tool denial |
| The role maps to another model | The resume uses the current model of the role. | info log with both model IDs |
| No usable provider | The run ends `Failed`, `model_unavailable`. No parked call runs. | result notice |
| The agent profile was deleted | The run ends `Failed`, `agent_definition_unavailable`. No parked call runs. | result notice |
| Subagents disabled, no tools, no `file_read`, no spawner | The run ends `Failed` with the spawn reason. No parked call runs. | result notice |
| A replayed prompt fails to post | Warning log. The request stays open. | warning log |

### Security boundary

The resumed call is a new dispatch, so it passes the whole gate again. Five facts bound it:

1. **Authority.** The requester, boundary, channel, and delivery targets come from `run.OriginalContext`. The path facts
   come from `park.PreBatchScope`. Neither ever comes from the current turn or from the current parent state.
2. **Trust.** The audience is the narrower of the recorded audience and the audience that the session derives now for
   the recorded requester and channel. A resume never widens trust.
3. **Policy.** The tool set and the gate use the current registry and `ToolAccessPolicy`. A deny rule, a protected path,
   or a narrower audience after the park wins over the answer. A tool whose fingerprint changed does not run on the
   old answer.
4. **Consent.** The only consent is the journaled answer of the run requester, validated by `ValidateResponse`. It is
   a one-time consent for the exact candidate keys of that prompt and that child call ID, used once.
5. **At most once.** `Resumed` persists before the call runs. The parent model has no path that produces an answer.

Positive example: Alice's child parks on `gh pr merge 42 --squash`. An operator adds an MCP server, and the daemon
restarts. Alice clicks "Approve once" on the old Slack prompt. The session persists the answer and `Resumed`, starts the
child under Alice's recorded authority, and `gh pr merge 42 --squash` runs once in the recorded working directory.

Negative examples:

- After the park, the operator adds `gh pr merge` to the shell deny list and reloads. Alice approves. The gate denies
  the call. Nothing runs.
- Bob clicks Alice's button after the restart. `ValidateResponse` reads the requester from the hydrated run record and
  rejects Bob (`approval_wrong_requester`). Nothing is recorded and nothing runs.
- After the park, the operator removes Alice from the operator list. At resume the derived audience is narrower than
  the recorded one, so the child runs with the narrower audience. A tool that only the operator audience may use is
  not in the tool set, and the parked call to it does not run.
- After the park, the parent session changes its project directory. The resumed call still runs in the recorded
  pre-batch directory.
- After the restart, the MCP server of the parked tool is replaced by another server with a tool of the same name. The
  fingerprint differs, so the call does not run on the old answer. A new prompt appears.
- The daemon crashes after `Resumed` and before the call result. Recovery records the run as lost. The call does not
  run a second time.

### Owners and data

| Decision | Owner | Data |
| --- | --- | --- |
| Park a batch, build the park | `SubAgentActor` | actor-local until sent |
| Accept a park, number the prompts, size cap | `LlmSessionActor` (Children partial) | PR 1a actor-local; PR 2 durable `ChildRunEvent.Parked` |
| Prompt shape | shared prompt builder | call-local |
| Answer validity | `SessionToolWorkflow.ValidateResponse` (unchanged) | durable `ToolApprovalResolved` |
| Resume gate | `LlmSessionActor.TryResumeParkedRun` | PR 2 durable `ChildRunEvent.Resumed` |
| Resume preparation, spawn rules, narrower audience | `SubAgentSpawner.PrepareResume` with the session trust deriver | call-local |
| Apply one plan entry to a call context | one helper, used by `SessionToolExecutionPipeline` and `SubAgentActor` | call-local |
| Fingerprint check, batch completion, loop-guard restore | `SubAgentActor` (incarnation 2) | actor-local from the park |
| Open entries, pause | `SessionToolWorkflow` | actor-local projection, hydrated from the run record |
| Recovery choice for each run | `LlmSessionActor.RecoverChildren` | durable lifecycle |
| Prompt replay | `LlmSessionActor` join handler | actor-local |
| Dedupe and replay-failure rule | `ChannelOutputEngine` (PR 2); `ChatViewModel`, `HeadlessChannel` (PR 3) | binding journal; memory |

### Alternatives rejected

| Alternative | Why rejected |
| --- | --- |
| Keep withdrawing (today) | It breaks the steady state that the owner chose. The operator comes back to withdrawn prompts and lost work after every config reload. |
| Child journal (D8, P7) | A second persistence system with a purge actor, tombstones, an orphan repair, and SQLite-only proof. The run record is durable already. |
| Park only at a graceful drain | Two resume paths. A crash loses the child. |
| Live wait plus a park record | Two resume paths, and the answer-before-park race comes back. |
| Freeze the model and the tool set | Needs stored schemas and an exact model lookup. A fingerprint gives the safety part at a small cost. |
| Run each child as a full `LlmSessionActor` | It reuses all durable approval code, but it changes the child execution model, the catalog, and the log partition. Much larger than this issue. |
| A conversation file in the run folder | A second store with no atomic link to the run record. |

### Stack and size

S1 split, with PR 1 in two parts (owner approval, 22 October 2026). Each PR starts with failing tests. `tasks.md` names
each test, its assertion, and its PR. All PRs stack on `feature/durable-child-consent-wait`.

| PR | Content | Format | Estimate (code plus tests) |
| --- | --- | --- | --- |
| PR 1a | The child stops and the owner keeps its park in memory. The resume uses the transcript (serializable list, M12), the checkpoint with the round seed (H1), and the loop guard (M10). The resume gate, the answer path with no live wait and its requester binding, the narrower audience (M6), the fingerprint check (M7), the profile and spawn failures, journal failure of the park (H4), cancel of a parked run, the shared plan-entry helper (M11), the shared prompt builder. The existing restart rules apply. | none | 1,200 to 1,700 lines |
| PR 1b | The receipt facts of the finished calls and their effects in the live order (H2), the pre-batch run scope (H3), and the managed temporary retry target, denial directory, and correction state (M1). | none | 600 to 900 lines |
| PR 2 | Durable `Parked` and `Resumed`, the proto, recovery, hydration, `AllowsSnapshot`, passivation, restart-reminder eligibility, held input at recovery, the size cap, prompt replay with dedupe and the replay-failure rule in the channel bindings, the downgrade note. Needs #2587. | forward-only | 1,300 to 1,900 lines |
| PR 3 | TUI and headless dedupe, the typed-key test, the native smoke run. | none | 250 to 400 lines |

Caution: PR 1a must not reach `dev` alone. Without PR 1b, a resume can lose the working-directory change of a sibling
call, run a parked call in the wrong directory, or drop a managed temporary retry. The feature branch merges to `dev`
only after PR 1b lands.

PR 1a and PR 1b together are larger than the earlier single PR 1 estimate, because each part needs its own stubs and
review fixes. Most of their lines are tests: each fact of the park needs a test that fails when the fact is lost.

### Tests and Stryker

- The first tests compile against stub types (`ChildRunPark`, `ChildParkReached`, `ResumeSubAgent`, and an owner handler
  that keeps today's behavior), so they fail on assertions, not on compile errors.
- Stryker target `child-resume-gate` (PR 1a, `child-consent` group, about 3 minutes). Spans with boolean guards only:
  the open-request check and the drain check of `TryResumeParkedRun`, the refusal branch of the plan-entry helper,
  the rule that a park is accepted only for an owned `Accepted` run, and the checkpoint seed.
- Stryker target `child-park-recovery` (PR 2, same group, about 4 minutes): the decision in `RecoverChildren` for each
  lifecycle, the child-entry rule of `AllowsSnapshot`, and the park lines of the snapshot mapper in both directions
  (`NetclawProtoMapper.CoordinationV2`, `ToProto` and `FromProto` of the park). The mapper span covers the field
  assignments, so a dropped assignment is a surviving mutant, and CI fails. The exhaustive round-trip test kills
  these mutants. The group then takes about 15 minutes, under the 25-minute
  job timeout.
- The security tests, not Stryker, guard the authority source and the order "persist `Resumed`, then spawn". A mutant
  there changes an object or a call order, which a boolean-guard span does not cover well.
- PR 1b adds no Stryker target. Its rules choose a data source (the recorded scope, the recorded receipts, the recorded
  retry target), not a boolean guard. PR 1b reviews the mutation scope, and its H2, H3, and M1 tests guard these
  rules.
- PR 3 adds no Stryker target. Its skip rule is a display rule with no authority.

## Risks / Trade-offs

- [The session journal and snapshot hold the child conversation while the run is parked.] → Only parked runs write it.
  `Resumed` drops it from the state. The size log and the cap bound each park.
- [The pause blocks the routine snapshot while it holds a pending input.] → The journal keeps the park events until the
  pause ends or a compaction snapshot runs. The cap bounds each event.
- [The prompt appears later in a batch with a slow sibling.] → The delay is bounded by that sibling.
- [Forward-only format from PR 2.] → See the downgrade text. PR 1a and PR 1b have no format change.
- [The resumed child gets a new system message, and reasoning content is not kept.] → The parent session has the same
  behavior after a restart.
- [The managed temporary correction state needs care.] → It changes which call the gate sees. The park records the
  state before the batch, each retry target, and each denial directory, and the resume restores them. Tests cover the
  retry and the denial hint.
- [After a crash between `Parked` and the prompt output, the prompt shows only at the next join.] → The window is one
  journal write. The parent notice lists the open request.
- [A running child is still lost at a restart.] → Out of scope.
- [Conflict with the open change `child-approval-events-and-parent-pause`.] → Its D8, its PR 3 tasks, and its park and
  purge deltas must leave that change before it syncs (task 1.2). Requirement names here differ.
- [Conflict with `subagent-explicit-model-selection`.] → If that change records an explicit model, `park.Spawn` carries
  it in place of the role.
- [Conflict with `stop-repeated-tool-cycles` and `streaming-tool-call-execution`.] → The park point waits for every call
  of the response. The park carries the loop guard.

## Migration Plan

1. Merge this spec PR. The owner confirmed the decisions in Context on 22 October 2026.
2. Remove the D8 material from `child-approval-events-and-parent-pause` with the OpenSpec skills, then sync and archive
   that change.
3. PR 1a, then PR 1b, into `feature/durable-child-consent-wait`. The feature branch merges to `dev` only after PR 1b
   lands. No format change. Rollback with the binary.
4. Merge #2587. Then PR 2 (forward-only). Publish the downgrade text in the release notes. Back up `netclaw.db`.
5. PR 3. Rollback to PR 2 is safe.
6. Sync this change and archive it.
