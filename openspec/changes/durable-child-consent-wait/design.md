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
| B3 | Resolve the tools under the current policy. Check a fingerprint for each parked call: the tool name, the MCP server ID, a hash of the transport identity, and a hash of the parameter schema. On a mismatch, park the call again with a new prompt. |
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
| `OpenBatch.Settled` | for each call that did not park: its result message and its receipt facts (file activity, declared project directory, exposure request tool name, cycle outcome category, result hash, pending-job flag, synthetic flag, authorization attempt ID), plus its model-input media references | these calls ran and never run again; their effects apply at batch completion (H2) |
| `OpenBatch.RepeatNudge` | the repeat nudge of the batch, or none | it comes after all results |
| `PreBatchScope` | project directory, inherited working directory, recent files, model input modalities, default and requested delivery targets | the parked calls run with the scope that the live batch had (H3) |
| `Checkpoint` | `ChildRunCheckpoint` before the batch: round, summary, file activity | the existing checkpoint type |
| `LoopGuard` | tool iteration and call counts, up to six completed action and outcome hashes, the last blocked action, the recurrence episodes, the refused-repeat count | a park does not reset the loop guard (M10); the checkpoint round stays monotonic (H1) |
| `LoadedTools` | the names of the deferred tools that the child loaded | `_loadedDeferredToolNames` |
| `Spawn` | agent name, agent prompt with the skill overlay, model role, findings flag, three budgets, the resolved model ID (for the log) | the run record does not have them |
| `Requests` | one `ToolApprovalRequested` per parked call. Each has the display text, the messy flag, the managed temporary retry target (the managed directory and the platform temporary root), the denial directory, and the tool fingerprint. Later its `ToolApprovalResolved` joins it. | the facts that the gate made, and what the resume needs to check and to seed consent |

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
  Persist ToolApprovalResolved(p)                    # the callback also applies it to r.Park.Requests (live and replay)
    emit ToolInteractionClosed(Answered); digest; pause check   (unchanged)
    TryResumeParkedRun(r)

TryResumeParkedRun(r):
  if r has no park: return
  if any request of r.Park is open: return           # guard 1: a batch with two parked calls waits for both
  if a restart drain is pending: return              # guard 2: nothing ran. PR 2: recovery resumes.
                                                     #   PR 1a and PR 1b: the restart records the run as lost.
  plan := SubAgentSpawner.PrepareResume(r, park)     # D4; no side effects
  if plan failed: RecordChildTerminal(r, Failed(reason), "The parked calls did not run.")
  else Persist ChildRunEvent.Resumed(r)              # PR 2; PR 1a removes the actor-local park instead
    on success: ActorOf(plan.Props, "subagent-{agent}-{runId}-{PromptsIssued}"); Watch; TrackChild; Tell ResumeSubAgent
    on rejection: Self.Tell(ResumeNotRecorded(r)) -> RecordChildTerminal(r, Failed(resume_not_recorded), ...)
    on failure: the session actor stops (Akka.Persistence); recovery decides
```

- The answer path needs no live wait. `PrepareLiveResponse` and its claim stay for parent calls only.
- **The live answer updates the park.** The persist callback of `ToolApprovalResolved` applies the answer to
  `r.Park.Requests` in the session state, and also to `_toolWorkflow`. Today the live path updates only
  `_toolWorkflow` (`LlmSessionActor.cs:3755`). Without this step, a compaction snapshot after the answer would hold
  the park with an open request, and the delete after it would drop the answer. Replay applies the same event in the
  same way.
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

**Journal failure and refusal (H4, N7, N9).** Each refusal has its own typed reason. The owner records the terminal
itself, and the child never maps an Ask failure to "cancelled".

- The owner keeps the waiting child's sender for each run that has a park in flight (actor-local
  `_parksInFlight[runId]`). It replies to that sender from the persist callback or from `OnPersistRejected`.
- **Entry lifetime of `_parksInFlight`.**
  - The owner adds the entry just before the `Persist` or `PersistAll` call of the park.
  - The owner removes the entry in exactly two places: in the callback of the LAST event of the park, or at the FIRST
    `ParkNotRecorded`.
  - A rejected `PersistAll` of N events calls `OnPersistRejected` N times, so N `ParkNotRecorded` messages come. Each
    `ParkNotRecorded` after the first finds no entry and does nothing. The owner therefore replies once and records
    the terminal once.
  - The owner replies `ChildParkAccepted` only from the callback of the last event.
  - A refusal that needs no write (the run is cancelling, not owned, or above the cap) adds no entry. The owner
    replies and records at once.
  - Test: `A_rejected_two_request_park_replies_and_records_once` (PR 1a).
- The reply is `ChildParkRefused(reason)` or `ChildParkAccepted`. On `ChildParkRefused` the child stops and sends no
  result. The owner already recorded the terminal. A later `ChildTerminalReturned` from that child finds the run ended,
  and `RecordChildTerminal` ignores it (existing guard).

| Case | Owner action | Outcome |
| --- | --- | --- |
| The run is cancelling | reply `ChildParkRefused(cancelled_by_parent)`; record `Cancelled` | the run ends cancelled; no prompt |
| The run is not owned or not accepted | reply `ChildParkRefused(spawn_unavailable)`; no record | the existing owner checks apply |
| The park is above the cap | reply `ChildParkRefused(park_too_large)`; record `Failed` | no prompt; no parked call runs |
| The journal rejects the park (PR 1a: the request events; PR 2: `Parked`) | from `OnPersistRejected`: `Self.Tell(ParkNotRecorded(runId))`, then reply `ChildParkRefused(park_not_recorded)` and record `Failed` | the notice says "The child could not wait for consent. The calls that needed consent did not run." |
| The journal fails the park | the session actor stops (Akka.Persistence) | the run is `Accepted` in the journal, so recovery records it as lost; no prompt was shown |
| The journal rejects `Resumed` (PR 2) | `Self.Tell(ResumeNotRecorded(runId))`, as `PersistRejectedTurnFailure` does | the run ends `Failed`, `resume_not_recorded`, "The parked calls did not run."; no actor starts |
| The journal fails `Resumed` (PR 2) | the session actor stops | recovery finds the park with all answers and no `Resumed`, and resumes it |

### D4. A resume is a spawn from a recorded conversation

The rule: **the spawn arguments and the path facts are recorded; the environment resolves again; the authority comes
from the run record, narrowed by the current trust.**

| Fact | Source at resume |
| --- | --- |
| Requester, boundary, channel, delivery targets | `run.OriginalContext` and `park.PreBatchScope`. Never the current turn, never the current parent. |
| Audience | the narrower of the recorded audience and the audience that the session trust deriver gives now for the recorded requester and channel (M6) |
| Project directory, inherited working directory, recent files, modalities | `park.PreBatchScope` (H3) |
| Agent prompt, overlay, model role, findings flag, budgets | `park.Spawn`. The agent profile must still exist in the registry, or the resume fails with `agent_definition_unavailable`. |
| Conversation, open batch, loop guard, loaded tools, managed temporary retry targets and denial directories | the park |
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
4. Checks the fingerprint of each parked call (B3, M7, N3, N12). The fingerprint has four parts:
   - the tool name (`mcp__{server}__{tool}` for an MCP tool);
   - the MCP server ID, or none for a native tool;
   - a hash of the transport identity: the transport and the URL for an HTTP server, or the transport, the command,
     the arguments, and the names of the environment variables for a stdio server. Values of environment variables
     and headers are never hashed, because they hold secrets;
   - a hash of the parameter schema only. The description text is not in the hash, so a description change in an
     upgrade does not cause a new prompt.

   On a mismatch the call does not run. The child parks that call again, and the session shows a new prompt. The old
   answer does not apply. Example: the operator points the same server key at a new endpoint with the same tool name
   and the same schema. The transport hash differs, so the call parks again.

   Implementation note: the mismatch warning logs only which parts differ (for example `transport_hash`). It never
   logs the URL, the command, the arguments, or a hash input.
5. Canonicalizes the parked calls as `HandleToolCalls` does, and runs only those calls through `ExecuteToolsAsync`,
   with the pre-batch scope. One helper applies one plan entry to a call context. The parent pipeline
   (`SessionToolExecutionPipeline.cs:538-575`) and the child both call it (M11):
   - approved: seed the one-time consent from `OneTimeApprovalKeys.Create(patterns, candidates, cwd)` of the journaled
     request, restore the authorization attempt ID, and mark the managed temporary retry target with its two recorded paths (M1,
     N4);
   - refused: no dispatch; the refusal text of `ConsentRefusalText.For`, with the managed temporary denial hint when
     the request has a denial directory (M1).
6. Completes the batch in the live order (H2): the result messages in call order, then for each result in call order
   its receipt effects (file activity, project directory, exposure, `load_tool`, cycle outcome), then the media nudge, then the repeat nudge. A sibling `set_working_directory` therefore applies after the
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
- **Held input (M5, N8).** When the pause holds after recovery, `RecoveryCompleted` puts the held inputs in the waiting
  queue before Akka.Persistence unstashes the commands that came during recovery. It splits them first with
  `SessionInputFlow.SplitByAuthority` and logs each damaged input, as `StartInputsHeldByThePause` does. A damaged input
  stays pending and never throws inside recovery. A stashed fire of the same reminder
  then finds the waiting fire, and the replace rule closes the older fire. One reminder runs once. When the pause ended
  at recovery (only withdrawn requests), the existing `StartHeldInputs` path stays.
- **A restart reminder respects the pause (N1).** `TryResumeInterruptedInput` (`LlmSessionActor.cs:2385-2446`) checks
  only `HasParkedApprovals()`, which counts parent approvals only. Held fires are pending inputs, so a drain makes the
  session eligible, and after recovery the reminder could start a turn for held automated input while the pause holds.
  New rule: while `IsChildConsentPaused` is true, the resume leaves out every input that the pause holds (the one
  predicate below). Those inputs are in the waiting queue already (M5). When no input that the pause does not hold
  remains, the session acknowledges the reminder with no turn (`AckRestartResumeNoOp`). An interrupted input that the
  pause does not hold still resumes. That set holds a person's input and also `UntrustedExternal` input. This rule
  depends on #2587, which changes the same method.
- **One pause predicate.** Today two copies of one security rule decide which input the pause holds:
  `SessionInputFlow.IsHeldByPause(SendUserMessage)` for the drain (`SessionInputFlow.cs:594-597`), and
  `WasHeldByPause(InputAdmitted)` for recovery (`LlmSessionActor.Children.cs:548-551`). They agree now. The design makes
  one static predicate, `ChildConsentPause.Holds(TurnContext authority, bool isChildNotice)`:

  ```text
  Holds(authority, isChildNotice) :=
      isChildNotice
      or (IsAutomation(authority.RequesterPrincipal) and not authority.IsChildConsentNotice)
  ```

  - The caller parses the recorded authority first. An input whose authority does not parse is split off before
    (`SplitByAuthority`), logged, and kept pending. The predicate never sees it.
  - `IsHeldByPause`, `StartInputsHeldByThePause` (through `WasHeldByPause`), the recovery restore of held input (M5),
    and the N1 restart-resume split all call it.
  - The predicate is a pure refactor of the two copies, so it lands in PR 0. PR 2 uses it for the N1 split.
  - Tests: PR 0 runs the drain path and the recovery path over one set of inputs and asserts the same held set. PR 2
    adds the restart-resume path to the same test.
- **Restart reminder (M2).** `UnreportedRuns()` does not count a parked run with an open request: no turn can run for
  it, and a click wakes the session. A parked run with all answers counts, because recovery must resume it.
- **Snapshots and the journal delete.** After `SaveSnapshotSuccess`, the session deletes the journal rows up to the
  snapshot (`LlmSessionActor.cs:2649`). The snapshot is then the only copy of a park. Recovery rebuilds the park from
  the snapshot. The snapshot holds the park in the run record: the `parked` lifecycle case (field 21 of
  `BackgroundChildRunV2Proto`) carries the whole park, and its `requests` carry each request with its answer. The next
  snapshot after `Resumed` no longer holds the park, so the delete after that snapshot removes the old `Parked` rows.
  A mapper that stops writing one park field would lose that field with no error, so two guards protect the mapper:
  an exhaustive round-trip test (task 10.11) and the mapper span of the Stryker target `child-park-recovery`.
- **Snapshots (M8).** `AllowsSnapshot` ignores child entries, because the run record holds them. The pause still
  blocks the routine snapshot when it holds a pending input, because `SaveSnapshotIfSafe` skips while any durable
  input is pending (`LlmSessionActor.cs:3509`). Then the journal keeps every park event until the pause ends or a
  compaction snapshot runs. The size log and the cap of D2 bound this.
- **Passivation.** A click wakes the session, and the answer handler resumes the child.
- **Recovery cost checks (PR 2, owner approval, 22 October 2026).** Parks make each recovery read and map more data.
  Two checks keep that cost visible:
  - A recovery-time test on the SQLite journal (the in-memory journal does not deserialize rows, so it hides the
    cost). It recovers two sessions with the same parent history: one with several parks at the 4 MiB cap, and one
    with no parks. A stopwatch measures each recovery from the actor start to a completion signal. The signal is the
    reply to a probe message that the session stashes during recovery, so the test waits with no sleep. Each recovery
    runs several times, and the test compares the fastest run of each. The PR 2 author measures both on the CI runner
    first, then records the measured numbers and the bound as constants with a comment. The bound is a ratio with a
    wide margin over the measured ratio, plus a small absolute floor, so normal CI noise does not fail the test. The
    test fails when the recovery with parks takes far longer than the bound allows.
  - A recovery log line. At `RecoveryCompleted`, when one or more parked runs exist, the session writes one Info line:
    `child_parks_recovered count={count} bytes={bytes}`. The bytes are the total serialized size of the parks. The
    line holds no content, only the two numbers. With no parked run, the session writes no line.
- **Prerequisite.** #2587 must merge before PR 2. A restart resume must queue every pending input, so the held input
  of a paused session does not stay with no turn. The N1 rule edits the same method, `TryResumeInterruptedInput`, so
  PR 2 rebases on #2587.

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
- **A replayed prompt that fails to post (M4, N2).** The replayed `ToolInteractionRequest` carries `Replayed = true`.
  The binding splits the failure in two:
  - A deterministic failure: the channel cannot render the prompt at all, or the channel API refuses the post for a
    permanent reason. The binding sends `prompt_unavailable`, and the session withdraws the request with
    `PromptUnavailable`. A pause with a prompt that no surface can show would hold the session with no end.
    Deterministic failures are:
    - a capability that the binding knows before it posts: a Mattermost binding with no callback URL cannot show
      buttons;
    - a permanent error code from the channel API, from the list of each binding (below).
  - A transient failure: every other post error, for example a transport error, a timeout, a rate limit, or a server
    error. The binding logs a warning and does not send `prompt_unavailable` (`SlackThreadBindingActor.cs:1092-1110`
    and the Mattermost and Discord equivalents). The request stays open, and the next join replays it again.

  Permanent error lists. Each binding keeps its list next to its existing error mapping. A code that is not in the list
  is transient.

  | Binding | Classification today | Permanent errors for a replayed prompt |
  | --- | --- | --- |
  | Slack | `SlackReplyClient.MapFailureKind` maps the Slack `error` code to `DeliveryFailureKind` | the codes that map to `ContentRejected` (`invalid_blocks`, `invalid_arguments`), `MessageTooLarge` (`msg_too_long`), `UnsupportedContent` (`too_many_attachments`), and `PermissionDenied` (`not_in_channel`, `channel_not_found`, `missing_scope`, `no_permission`); PR 2 adds `is_archived` to the `PermissionDenied` group, because today it maps to `Unknown` |
  | Discord | Discord.Net `HttpException` with `HttpCode` and `DiscordCode`; today the binding checks only `BadRequest` for the thread race | `HttpCode` 400 (for example `DiscordCode` 50035, invalid form body), 403 (50001 missing access, 50013 missing permissions), 404 (10003 unknown channel), and 413 (payload too large); 429 and 5xx stay transient |
  | Mattermost | no code-based classifier for post errors today; the connect classifier reads message text | the HTTP status of the failed post from Mattermost.NET: 400, 403, 404, and 413; 429, 5xx, and a network error stay transient. PR 2 adds a small `MattermostPostFailureClassifier`, and its first test pins the exception type that Mattermost.NET throws |

  Tests (PR 2): for each binding, one permanent code sends `prompt_unavailable`, and one transient error does not.

  A first post (not a replay) that fails keeps the existing rule: the session withdraws the request.
- The replay rebuilds `ToolInteractionRequest` from the parked request, with the new `display_text` and `is_messy`
  fields. Both fields land in PR 2 (L3). The labels come from the option keys (`ApprovalOptionKeys.LabelFor`).

### D7. Cancel and end of a parked run

- **Cancel.** `ControlChild` accepts a parked run. No actor must acknowledge. The owner writes `CancellationRequested`
  and `TerminalRecorded(Cancelled, cancelled_by_parent)` in one `PersistAll`. It withdraws the open prompts with
  `CancelledByParent`, writes `result.md`, and admits the result notice.
- **Park refused.** The owner accepts a park only for an `Accepted` run that it owns. Otherwise it replies
  `ChildParkRefused` with a typed reason and records the terminal itself (D3, N7).
- **Check.** `check_agent_run` shows `state: "parked"` and the open request count.
- **End.** A resumed run ends as any run ends. `Resumed` drops the park from the state. The journal rows go at the next
  snapshot, as every session event does.

### Formats (PR 2, forward-only)

| Message | Change | Field numbers (L6) |
| --- | --- | --- |
| `ChildRunEventV2Proto` | `fact` oneof: `ChildRunParkProto parked`, `bool resumed` | 8, 9 |
| `BackgroundChildRunV2Proto` | `lifecycle` oneof: `ChildRunParkedStateV2Proto parked`; field 20 (`source_reminder_id`) is not in the oneof, so the oneof skips it | 21 |
| `BackgroundChildRunV2Proto` | `int32 prompts_issued` | 22 |
| `ToolApprovalRequestedProto` | `optional string display_text`, `optional bool is_messy`, `ManagedTemporaryRetryTargetProto managed_temporary_retry_target` (two fields: `managed_temporary_directory = 1`, `platform_temporary_root = 2`), `ToolFingerprintProto tool_fingerprint` (four fields: `tool_name = 1`, `server_id = 2`, `transport_hash = 3`, `parameter_schema_hash = 4`) | 25, 26, 27, 28 |
| `ChildRunParkedStateV2Proto` | `ChildRunParkProto park`; `ChildRunParkProto` has `repeated ChildRunApprovalProto requests` (request and answer) and `int32` and message fields for the other park fields | 1 (park); field numbers of `ChildRunParkProto` are set in PR 2 |
| new | `ChildRunParkProto`, `ChildRunParkedStateV2Proto`, `ChildParkSettledResultProto`, `ChildParkScopeProto`, `ChildLoopGuardProto`, `ChildSpawnFactsProto`, `ManagedTemporaryRetryTargetProto`, `ToolFingerprintProto` | new messages, each with a manifest and a mapper entry |

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
| The journal rejects the park | The run ends `Failed`, `park_not_recorded`. No prompt. No parked call runs. | result notice |
| The journal fails the park | The session stops. Recovery records the run as lost. No prompt was shown. | lost notice |
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
| The tool's fingerprint changed (name, server, transport, or parameter schema) | The call does not run. The child parks it again. | new prompt and warning log |
| A restart reminder arrives while the pause holds | Held input stays in the queue. Only input that the pause does not hold resumes. | `restart_resume` log line |
| A replayed prompt cannot render in its channel, or the API refuses it with a permanent error | The session withdraws the request with `PromptUnavailable`. | withdrawal redraw |
| A replayed prompt post fails in transport | The request stays open for the next join. | warning log |
| The parked call's tool is gone | "Unknown tool: {name}". The child continues. | warning log (existing) |
| The trust of the requester is narrower now | The child runs with the narrower audience. A `Public` audience fails the spawn rule. | `spawn_unavailable` or tool denial |
| The role maps to another model | The resume uses the current model of the role. | info log with both model IDs |
| No usable provider | The run ends `Failed`, `model_unavailable`. No parked call runs. | result notice |
| The agent profile was deleted | The run ends `Failed`, `agent_definition_unavailable`. No parked call runs. | result notice |
| Subagents disabled, no tools, no `file_read`, no spawner | The run ends `Failed` with the spawn reason. No parked call runs. | result notice |

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
- After the park, the operator points the same MCP server key at a new endpoint. The tool name and the schema stay the
  same. The transport hash differs, so the call does not run on the old answer. A new prompt appears.
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

The owner approved this split on 22 October 2026, with PR 0 and the move of H3 into PR 1a from the second review.
Each PR starts with failing tests. `tasks.md` names each test, its assertion, and its PR.

| PR | Base | Content | Format | Estimate (code plus tests) |
| --- | --- | --- | --- | --- |
| PR 0 | `dev` | Pure refactors with no behavior change: the shared prompt builder, the shared plan-entry helper (M11), the export and restore of the `TurnStateTracker` loop guard, and the one pause predicate `ChildConsentPause.Holds`. It can merge to `dev` at once. | none | 350 to 550 lines |
| PR 1a | `feature/durable-child-consent-wait` (made from `dev` after PR 0) | The child stops and the owner keeps its park in memory. The transcript (M12), the checkpoint seed (H1), the loop guard (M10), the pre-batch run scope (H3), the resume gate, the answer path with no live wait and its requester binding, the live answer applied to the park, the narrower audience (M6), the fingerprint (M7, N3, N12), the typed park refusals (N7), the profile and spawn failures, cancel of a parked run, and the `docs/architecture/tool-authorization.md` update. The existing restart rules apply. | none | 1,300 to 1,800 lines |
| PR 1b | `feature/durable-child-consent-wait` | The receipt facts of the finished calls and their effects in live order (H2), and the managed temporary retry target and denial directory (M1, N4). | none | 400 to 650 lines |
| Merge PR | `dev` | `feature/durable-child-consent-wait` into `dev`, as its own reviewed PR, verified on the combined stack. | none | no new lines |
| PR 2 | `dev` (after the merge PR and #2587) | Durable `Parked` and `Resumed`, the proto, recovery, hydration, `AllowsSnapshot`, passivation, restart-reminder eligibility and the pause rule (N1), held input at recovery (M5, N8), the size cap, prompt replay with dedupe and the replay-failure rules (M4, N2), the mapper guards, the recovery cost checks, the downgrade note. | forward-only | 1,650 to 2,250 lines |
| PR 3 | `dev` (after PR 2) | TUI and headless dedupe, the typed-key test, the native smoke run. | none | 250 to 400 lines |

Caution: PR 1a must not reach `dev` alone. Without PR 1b, a resume can lose the working-directory change of a sibling
call or drop a managed temporary retry. The feature branch merges to `dev` only after PR 1b lands, through its own
reviewed merge PR.

PR 1a still changes a consent surface and the resume path, so it carries the architecture document update in the same
diff (CLAUDE.md, "Architecture Document Rule"). It also records the pre-batch scope, so a resume never builds its scope
from a wrong source, also on the feature branch.

The total is now 3,950 to 5,650 lines over five code PRs. Most of the lines are tests: each fact of the park needs a test
that fails when the fact is lost.

### Tests and Stryker

- The first tests compile against stub types (`ChildRunPark`, `ChildParkReached`, `ResumeSubAgent`, and an owner handler
  that keeps today's behavior), so they fail on assertions, not on compile errors.
- Stryker target `child-resume-gate` (PR 1a, `child-consent` group, about 3 minutes). Spans with boolean guards only:
  the open-request check and the drain check of `TryResumeParkedRun`, the refusal branch of the plan-entry helper,
  the rule that a park is accepted only for an owned `Accepted` run, and the checkpoint seed.
- Stryker target `child-park-recovery` (PR 2, same group, about 4 minutes): the decision in `RecoverChildren` for each
  lifecycle, the child-entry rule of `AllowsSnapshot`, and the park lines of the snapshot mapper in both directions
  (`NetclawProtoMapper.CoordinationV2`, `ToProto` and `FromProto` of the park). The mapper span covers the field
  assignments, so a dropped assignment is a surviving mutant, and CI fails. Stryker covers object initializers only in
  part, so the real guard of the mapper is the exhaustive round-trip test (task 10.11). The Stryker span is a second
  check (N11). The group then takes about 15 minutes, under the 25-minute
  job timeout.
- The security tests, not Stryker, guard the authority source and the order "persist `Resumed`, then spawn". A mutant
  there changes an object or a call order, which a boolean-guard span does not cover well.
- PR 1a must pass its own gate. Its drain span needs a PR 1a test: `An_answer_during_a_drain_does_not_resume` (the
  answer is recorded, and no child starts). The drain guard therefore stays in `child-resume-gate` (N5).
- PR 0 adds no Stryker target. It changes no behavior, and the existing tests of the moved code stay green.
- PR 1b adds no Stryker target. Its rules choose a data source (the recorded receipts and the recorded retry target),
  not a boolean guard. PR 1b reviews the mutation scope, and its H2 and M1 tests guard these rules.
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
- [The managed temporary correction state starts empty at resume.] → The parent never persists it either, and
  `ClearApprovalTurn` clears it at each turn end. The park keeps only what the parked calls need: each retry target
  (two paths) and each denial directory. Tests cover the retry and the denial hint.
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
3. PR 0 into `dev`. No behavior change.
4. Make `feature/durable-child-consent-wait` from `dev`. PR 1a, then PR 1b, into it. No format change.
5. The merge PR: `feature/durable-child-consent-wait` into `dev`, reviewed on its own. Rollback with the binary.
6. Merge #2587. Then PR 2 into `dev` (forward-only). Publish the downgrade text in the release notes. Back up
   `netclaw.db`.
7. PR 3 into `dev`. Rollback to PR 2 is safe.
8. Sync this change and archive it.
