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
   (`:178-190`), so a new bridge would reuse the numbers.
3. **The request is journaled.** `SessionToolWorkflow.ChildApprovalCapability` sets `PersistApprovalState = true`,
   `SourceChildRunId`, and `ChildContext` (`SessionToolWorkflow.Children.cs:171-187`).
   `HandleToolInteractionRequestDispatch` persists `ToolApprovalRequested` (`LlmSessionActor.cs:3408-3442`). The event
   holds the requester, options, patterns, candidates, working directory, turn context, `SourceChildRunId`, and
   `OriginalChildCallId`. It holds no display text.
4. **The answer needs the live wait.** A child answer goes to `HandleProcessingApprovalResponseAsync`, also when the
   session is idle (`LlmSessionActor.cs:3778-3783`). `PrepareLiveResponse` must claim the live wait in
   `ApprovalChannel`. With no wait it rejects with `no_live_wait` (`SessionToolWorkflow.Approvals.cs:220-242`).
5. **The child conversation is in memory only.** `_history` (`SubAgentActor.cs:108`) holds the system prompt, the task
   message, the assistant tool calls, the tool results, and the nudges. The child also holds the loaded deferred
   tools, the project directory, the file activity, and the spawn facts (agent prompt with a skill overlay, model role,
   findings flag, and budgets). `ChildCheckpointReached` gives the owner only the round, a summary, and the file
   activity (`:917-979`).
6. **The run record is durable.** `BackgroundChildRun` (`BackgroundChildRun.cs:22-42`) holds the run ID, agent,
   `OriginalContext` (the requester authority), the initial working snapshot, and the lifecycle. `ChildRunAccepted` and
   `ChildRunEvent` are in the session journal. The session snapshot holds every run record
   (`SessionSnapshotV2Proto.child_runs`).
7. **The parent already resumes its own parked batch.** For its own calls the session journals the batch
   (`ToolBatchStarted`, `ToolCallRecorded`) and the approval. After an answer, also after a restart or passivation, it
   re-drives only the parked calls with an `ApprovalRedrivePlan` (`SessionToolWorkflow.cs:378-437`,
   `LlmSessionActor.cs:3840-3850`). The plan seeds a one-time consent from the journaled patterns, candidates, and
   working directory.
8. **Recovery withdraws.** `RecoveryCompleted` withdraws every child entry that replay left open
   (`WithdrawRecoveredChildApprovals`, `LlmSessionActor.cs:265`), and `RecoverChildren` records each `Accepted` or
   `Cancelling` run as lost (`LlmSessionActor.Children.cs:451-460`). The session keeps the closes in
   `_restartWithdrawals` and sends them to each new subscriber (`LlmSessionActor.cs:2597`).
9. **Snapshots.** `SessionSnapshot` excludes approval state. `AllowsSnapshot` refuses a routine snapshot while any
   approval entry is open (`SessionToolWorkflow.cs:226-228`). The compaction snapshot has no such check
   (`LlmSessionActor.cs:1034`).
10. **Passivation.** A live child keeps the session in memory (`HasUnreportedChildren`, `LlmSessionActor.cs:1782`). An
    open parent approval does not.

### Constraints

- One owner for each decision. The session actor owns the run record and every decision about it.
- Reuse before you add. Anchor on the run record, the session journal, and the parent re-drive plan.
- No second persistence system. No new persistence ID.
- Default deny. No silent fallback. A resume that cannot meet a spawn rule fails loudly.
- Persisted types are proto messages with a manifest and a mapper entry.
- Tests are deterministic, with no sleep.

## Goals / Non-Goals

**Goals:**

- A child that parked at consent survives a restart, a config reload, a compaction snapshot, and passivation.
- One resume path for a live answer and for an answer after a restart.
- The resumed call runs at most once, under the run authority, and it passes every gate again.
- The pause and the prompts come back after recovery with no new pause logic.

**Non-Goals:**

- The resume of a running child that did not park. Recovery records it as lost, as today.
- A frozen model or a frozen tool set. The resume resolves both again, as a spawn does (D4).
- A change to the approval notice, the pause rules, the withdrawal texts, or the text answer guard of
  `child-approval-events-and-parent-pause`.

## Decisions

### D1. Park always

A child call that needs consent does not wait. The child records a parked marker for the call and lets the other calls
of the batch finish. When `Task.WhenAll` ends and one or more calls parked, the child sends its park to the owner and
stops after the acknowledgement.

Why: one resume path. A live answer and an answer after a restart start the same recreation. The restart path is then
the normal path, so tests and production exercise it all the time. The change removes code: the live child wait, the
watchdog pause for approval waits (`SubAgentApprovalWaitStarted`, `SubAgentApprovalWaitCompleted`,
`_pendingApprovalWaits`), and the child use of `ApprovalChannel`.

Rejected: a live wait plus a park only at a restart. That gives two resume paths. The restart path runs only at a
restart, so its defects hide. A crash gives no time to park.

Trade-off: the prompt appears when the batch ends, not while a slow sibling runs. The parent batch shows its prompt at
once. The delay is the run time of the slowest sibling call.

### D2. The park and its prompts are one event in the run record

The owner writes one new session event, `ChildRunEvent.Parked(ChildRunPark)`. It moves the run lifecycle from
`Accepted` to `Parked`. The owner emits the prompts only after the event persists. The park holds:

| Field | Content | Why the child cannot get it again |
| --- | --- | --- |
| `Transcript` | every history message after the system message, up to and with the assistant message of the open batch, as `SerializableChatMessage` | it exists only in `_history` |
| `SettledResults` | the result messages of the calls that did not park, as `SerializableChatMessage` | the calls ran; they must never run again |
| `SettledMedia` | the model-input media references of those results | the media nudge comes after all results of the batch |
| `RepeatNudge` | the repeat nudge of the batch, or none | the nudge comes after all results of the batch |
| `Checkpoint` | `ChildRunCheckpoint`: the round, a summary, and the file activity with the project directory | the existing checkpoint type |
| `LoadedTools` | the names of the deferred tools that the child loaded | `_loadedDeferredToolNames` |
| `Spawn` | the agent prompt (with the skill overlay), the model role, the findings flag, and the three budgets | the spawner had them; the run record does not |
| `Approvals` | one `ToolApprovalRequested` for each parked call, with the display text; later its `ToolApprovalResolved` | the request facts that the gate made |

Reuse: the proto uses `SerializableChatMessageProto`, `ChildRunCheckpointProto`, `SerializableMediaReferenceProto`, and the
existing `ChildRunApprovalProto { request, resolution }`. The run record is in the snapshot already, so the park, its
requests, and its answers survive a compaction snapshot with no new snapshot field.

The open batch follows the parent rule. The parent journals settled results and adds the nudge only when the batch
completes. The child does the same. No user message can come between two tool results, because a provider refuses
that history.

A parked call gets no message in `SettledResults`. The `OriginalChildCallId` of its request names it.

The owner, not the child, makes the prompt call IDs. It takes the numbers from a durable counter on the run record,
`PromptsIssued`, which the `Parked` event increments. A resumed run never reuses a number. The prompt call ID keeps
its form, `{runId}/subagent-approval/{n}`. One shared prompt builder (now inside `ParentSessionApprovalBridge.PromptAsync`)
makes the `ToolInteractionRequest` for the parent and for the child, so the prompt shape stays the same.

Rejected:

- A child journal (design D8 of `child-approval-events-and-parent-pause`, owner decision P7). It adds a persistent
  actor, a persistence ID scheme, five event types, a purge actor, a tombstone rule, an orphan-journal repair, a
  `HasChildJournal` flag, incarnation names, and a SQLite-only test. Every part exists to keep and then remove a
  second store. The run record is durable already.
- A conversation file in the run folder. It is a second store with no atomic link to the run record, and a crash
  between the file and the event leaves them out of step.
- Separate `ToolApprovalRequested` events plus a new snapshot field for parked entries (D8.7). Two durable copies of
  one fact. The run record carries the requests already.

### D3. Answers and the resume gate

```text
schematic: answer for a child prompt p of run r (live or after recovery; same handler)
  if p is not open: RejectClosedCall (existing texts)
  ValidateResponse(p, response)                      # requester binding, option (unchanged)
  record a session or persistent grant if the option asks (unchanged)
  Persist ToolApprovalResolved(p)                    # also applied to r.Park.Approvals
    emit ToolInteractionClosed(Answered); digest; pause check   (unchanged)
    TryResume(r)

TryResume(r):
  if r is not Parked: return
  if any request of r.Park is open: return           # a batch with two parked calls waits for both
  if a restart drain is pending: return              # recovery resumes; nothing ran
  plan  := ResumePlan(r)                             # see D4; no side effects
  if plan failed: RecordChildTerminal(r, Failed(reason), "The parked calls did not run.")   # loud
  else Persist ChildRunEvent.Resumed(r)              # lifecycle Parked -> Accepted(r.Park.Checkpoint)
    ActorOf(plan.Props, "subagent-{agent}-{runId}-{PromptsIssued}"); Watch; TrackChild
    Tell ResumeSubAgent(plan)
```

- The answer path needs no live wait. `PrepareLiveResponse` and its claim stay for parent calls only.
- `Resumed` persists before any parked call runs. A crash after `Resumed` is uncertain work: recovery records the run
  as lost and never runs the call again (existing rule).
- The preparation runs on the actor thread. It reads only configuration and the run record, and it has no await, so a
  cancel cannot come between the preparation and `Resumed`. The old design needed an off-thread step, a cancel check,
  and a wait for `Terminated`. This design needs none of them.
- The actor name has the prompt counter, so a new incarnation never collides with an old actor that still stops.

### D4. A resume is a spawn from a recorded conversation

The rule is short: **the spawn arguments are recorded; the environment resolves again; the authority comes from the
run record.**

| Fact | Source at resume |
| --- | --- |
| Requester, audience, boundary, channel, delivery targets | `run.OriginalContext` (durable). Never the current turn. |
| Agent prompt, overlay, model role, findings flag, budgets | `park.Spawn` (recorded) |
| Conversation and open batch | `park.Transcript`, `SettledResults`, `SettledMedia`, `RepeatNudge` |
| Project directory, file activity, round | `park.Checkpoint` |
| Model client | `IChatClientProvider.GetClient(park.Spawn.ModelRole)` now, as `SubAgentSpawner.PrepareRunAsync` does (`:246`) |
| Tool set | `SubAgentSpawner.ResolveTools` now, under the current registry and audience policy (`:379`) |
| Loaded deferred tools | each name in `park.LoadedTools` that `TryActivateDiscoveredTool` still accepts |
| System message | `BuildSystemPrompt` now, with the operating rules and project instructions read again, as the session does at recovery (`SetSystemPrompt`) |
| Consent capability | `SessionToolWorkflow.ChildApprovalCapability(run)` now |

`SubAgentSpawner` gets one method, `PrepareResume(run, park)`. It shares the scope and props builder with
`PrepareRunAsync`. It applies the spawn rules again, and each failure has a reason:

| Rule | Reason when it fails |
| --- | --- |
| `SubAgentConfig.Enabled` | `spawn_unavailable` (existing) |
| The audience is not `Public` | `spawn_unavailable` (existing) |
| At least one child tool under the current policy | `no_tools_available` (existing) |
| `file_read` is available to the requester | `no_result_reader` (existing) |
| The provider is not degraded (`IChatClientProvider.IsDegraded`) | `model_unavailable` (new) |

The resumed child starts with `_history` = the new system message, the transcript, and nothing else. It canonicalizes
the parked calls as `HandleToolCalls` does, and runs only the parked calls through `ExecuteToolsAsync` with a per-call
answer:

- Approved: the call context gets a one-time consent with the keys of `OneTimeApprovalKeys.Create(patterns,
  candidates, cwd)` from the journaled request, and the journaled authorization attempt ID. This is the parent
  re-drive plan (`ApprovalRedrivePlanBuilder.Add`), keyed by `OriginalChildCallId`. The session builds the plan with
  the same builder.
- Refused: the call does not run. It gets the same refusal text as the live path (`ConsentRefusalText.For`).

Then the child completes the batch: it adds the results in call order (settled and new), then the media nudge, then the
repeat nudge, and continues the model loop. The existing `ToolExecutionCompleted` handler does this work once the
settled results join the new results.

Why resolve the model and the tools again: the parent session does the same after a restart or a config reload. It
needs no stored schema, no exact-model lookup, and no rule for a stale frozen value. The current policy decides, which
is the default-deny direction. The owner decision P10 (frozen model) changes. See Open Questions.

### D5. Recovery keeps parked runs

```text
schematic: RecoveryCompleted, changed lines only
  replay: Parked applies its requests to SessionToolWorkflow (ApplyApprovalRequested); ToolApprovalResolved resolves them
  snapshot offer: HydrateParkedChildApprovals()       # same pattern as HydrateLegacyChildApprovals
  withdraw only the child entries whose run is not Parked     # DaemonRestarted etc., unchanged texts
  _restartWithdrawals := those closes only
  CloseRecoveredConsentNotices()                      # unchanged; the open parked requests rebuild the digest
  pausedAtRecovery := an open child request of a run that is Accepted or Parked
  RecoverChildren():
    Accepted or Cancelling        -> lost (unchanged)
    Parked, a request open        -> nothing; the run waits with no actor
    Parked, all answered          -> TryResume (D3); nothing ran
  ResumeOrphanNotices; StartHeldInputs if pausedAtRecovery (unchanged code)
```

- **The pause comes back by itself.** `IsChildConsentPaused` reads the open child entries in `SessionToolWorkflow`.
  Hydration puts the parked requests back, so the pause, the spawn refusal, and the reminder replacement rule apply
  again with no new code.
- **Held input.** `StartHeldInputs` queues the input that the pause held. `DrainBufferedBatch` gets
  `childConsentPaused = true`, so automated input and child result notices stay in the queue. The last answer runs
  `ChildConsentChanged`, then `StartWaitingInputAfterApproval`, as today. A person's interrupted input waits for the
  restart reminder (`d2-reminders-end-at-admission`, #2516), as today.
- **Orphan child notices** of other runs go into the queue (`ResumeOrphanNotices`). While the pause holds, they wait.
- **Notices.** If a notice was pending at the crash, recovery closes it, and the rebuilt digest is ready, so one new
  notice lists the open parked requests. If not, the rebuilt digest is a baseline and wakes nobody. This is the D4
  rule of the earlier change, with a non-empty open set.
- **Snapshots.** `AllowsSnapshot` ignores child entries, because the run record holds them. Parked prompts no longer
  block a routine snapshot. The compaction snapshot keeps them.
- **Passivation.** A parked run has no actor, so `HasUnreportedChildren` is false for it. A session whose only children
  are parked can passivate. A click wakes the session, and the answer handler resumes the child.
- **Answer during a drain.** The handler persists the answer and does not resume (D3). After the restart, recovery
  finds a park with all answers and no `Resumed`, and resumes it.

### D6. The prompt stays live in each channel

| Surface | After a restart | Change |
| --- | --- | --- |
| Slack, Mattermost, Discord | The binding journal restores the tracked prompt (`PendingApprovalPromptTracked` with `UntilClosed`). The buttons keep the call ID. A click reaches the recovered session, and the entry is open. | Recovery does not send `DaemonRestarted` for a parked prompt. The binding self-heal path does not fire, because the session accepts the answer. |
| TUI that stays open | `ChatViewModel` keeps `UntilClosed` prompts across a reconnect. A key answers the old call ID. | None. |
| TUI that starts again | The view model is empty. | PR 3: the session sends each open child prompt to a new subscriber at its join. `ChatViewModel` skips a call ID that it holds. |
| Headless | It prints a prompt and its close. It cannot answer. | PR 3: it prints a replayed prompt once (`_shownPrompts`). The exit rule stays with #2541. |
| Mattermost with no callback URL | A child prompt is refused with `PromptUnavailable` (buttons only). | None. A refusal closes the request, and the resume gives the call a refusal result. |

Prompt replay (PR 3) rebuilds `ToolInteractionRequest` from the parked `ToolApprovalRequested`. That needs two new
optional fields on `ToolApprovalRequestedProto`: `display_text` and `is_messy`. PR 1 writes them in the park, so
PR 3 has the data. The labels come from the option keys (`ApprovalOptionKeys.LabelFor`), as the text answer parser
does today. `ChannelOutputEngine.HandleApprovalRequestAsync` skips a call ID that `_pendingRequests` holds.

The withdrawal paths of the earlier change stay. They apply to a run that was live at the restart (it never parked, so
it showed no prompt in this design) and to legacy journal rows written by an older binary.

### D7. Cancel and end of a parked run

- **Cancel.** `ControlChild` accepts a `Parked` run. No actor must acknowledge. The owner writes
  `CancellationRequested` and `TerminalRecorded(Cancelled, cancelled_by_parent)` in one `PersistAll`. It withdraws the
  open prompts with `CancelledByParent`, writes `result.md`, and admits the result notice (existing paths).
- **Park refused.** When a park arrives for a run that is not `Accepted` (a cancel came first), the owner replies with
  a failure. The child completes as cancelled through its own cancel token (existing path).
- **Check.** `check_agent_run` shows `state: "parked"` and the open request count.
- **End.** A resumed run ends as any run ends. No purge step exists, because nothing outside the run record holds the
  conversation. After `Resumed`, the run record no longer holds the park. The journal rows go at the next snapshot, as
  every session event does.

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
    S->>J: Persist ChildRunEvent.Parked (transcript, batch, requests)
    S->>S: unwatch and release C1; open entries; pause; digest
    S-->>C1: ack, then C1 stops
    S->>Ch: ToolInteractionRequest (UntilClosed)
    Note over S,J: restart, config reload, compaction, or passivation
    J->>S: recovery: Parked run; requests open; pause back
    Op->>Ch: click "approve once"
    Ch->>S: ToolInteractionResponse (same call ID)
    S->>S: ValidateResponse (requester, option)
    S->>J: Persist ToolApprovalResolved
    S->>S: last open request of the park -> ResumePlan (spawn rules)
    S->>J: Persist ChildRunEvent.Resumed
    S->>C2: ActorOf; ResumeSubAgent(plan, run authority)
    C2->>C2: gate checks the parked call again; one-time consent; call runs once
    C2->>S: later: checkpoint or ChildTerminalReturned (existing)
```

### Failure modes

| Case | Outcome | Loud signal |
| --- | --- | --- |
| Crash before `Parked` persists | The run is `Accepted`, so recovery records it as lost. No prompt was shown. | lost notice (existing) |
| Crash after `Parked` | The run waits. The prompt stays live. | recovery log line `parked child run kept` |
| Crash after the last answer, before `Resumed` | Recovery resumes. Nothing ran. | resume log line |
| Crash or second restart after `Resumed` | Recovery records the run as lost. The call does not run again. | lost notice: "External effects after that checkpoint are uncertain." |
| Answer before the child is back | The answer persists and resumes the run. The old actor has no part. | none needed |
| Answer during a restart drain | The answer persists. Recovery resumes. | resume log line |
| Answer after `Resumed` | `RejectClosedCall`: "already has an answer". Nothing runs. | nack `approval_already_answered` |
| The policy now denies the parked call | The gate denies it at call time. The child gets the access-denied result. | denial result and audit log |
| The policy now needs a new consent (other candidates) | The one-time consent does not cover it. The child parks again with a new prompt. | new prompt |
| The parked call's tool is gone | The dispatcher gives "Unknown tool: {name}". The child continues. | warning log (existing) |
| A loaded deferred tool is gone or now hidden | It is not loaded. A call gets "Unknown tool". | warning log |
| The role now maps to another model | The resume uses the current model of the role, as a new spawn would. | info log with the role and the model |
| No usable provider (`IsDegraded`) | The run ends `Failed`, reason `model_unavailable`. The parked calls do not run. | result notice states the reason |
| Subagents disabled, or no tools, or no `file_read` | The run ends `Failed` with the spawn reason. The parked calls do not run. | result notice |
| The model call after the parked call fails | The run ends `Failed` (`llm_call_failed`), as a live run does. The call ran once. | result notice |
| The prompt cannot be posted | The session refuses the request (`PromptUnavailable`). The resume gives a refusal result. | withdrawal redraw (existing) |
| The agent profile was deleted after the park | No effect. The park holds the agent prompt. The operator can cancel the run. | none (see Open Questions) |

### Security boundary

The resumed call is a new dispatch, so it passes the whole gate again. Four facts bound it:

1. **Authority.** The resumed child runs under `TurnContext.FromRecord(run.OriginalContext)`. The current turn never
   lends its requester, audience, or boundary.
2. **Policy.** The tool set and the gate use the current registry and `ToolAccessPolicy`. A deny rule, a protected path,
   or an audience change after the park wins over the answer.
3. **Consent.** The only consent is the journaled answer of the run requester, validated by `ValidateResponse`. It
   becomes a one-time consent for the exact candidate keys of that prompt, for that child call ID, once. A session or
   persistent grant comes from the answer, as today.
4. **At most once.** `Resumed` persists before the call runs. The model of the parent has no path that produces an
   answer.

Positive example: Alice's child parks on `gh pr merge 42 --squash`. An operator adds an MCP server, and the daemon
restarts. Alice clicks "Approve once" on the old Slack prompt. The session persists the answer and `Resumed`, starts the
child under Alice's recorded authority, and `gh pr merge 42 --squash` runs once.

Negative examples:

- After the park, the operator adds `gh pr merge` to the shell deny list and reloads. Alice approves. The gate denies
  the call before consent. Nothing runs, and the child gets the access-denied result.
- Bob writes in the same thread while the child waits. Bob clicks Alice's button. `ValidateResponse` rejects him
  (`approval_wrong_requester`). Nothing is recorded and nothing runs.
- After the resume, the child model calls `gh pr merge 43 --squash`. The one-time consent covers only the candidate of
  call 42. The child parks again with a new prompt.
- The daemon crashes after `Resumed` and before the call result. Recovery records the run as lost. The merge does not
  run a second time.
- Bob's turn runs when Alice's child resumes. The resumed child gets Alice's audience and requester, not Bob's.

### Owners and data

| Decision | Owner | Data |
| --- | --- | --- |
| Park a batch, build the park | `SubAgentActor` | actor-local until sent |
| Accept a park, number the prompts | `LlmSessionActor` (Children partial) | durable: `ChildRunEvent.Parked` in the session journal; the run record in the snapshot |
| Prompt shape | shared prompt builder (from `ParentSessionApprovalBridge.PromptAsync`) | call-local |
| Answer validity | `SessionToolWorkflow.ValidateResponse` (unchanged) | durable `ToolApprovalResolved`, also in the run record |
| Resume gate | `LlmSessionActor.TryResumeParkedRun` | durable `ChildRunEvent.Resumed` |
| Resume preparation and spawn rules | `SubAgentSpawner.PrepareResume` | call-local |
| Re-drive plan for parked calls | `ApprovalRedrivePlanBuilder` (existing) | call-local, from durable requests and answers |
| Run the parked calls once | `SubAgentActor` (incarnation 2) | call-local |
| Open entries, pause | `SessionToolWorkflow` | actor-local projection, hydrated from the run record |
| Recovery choice for each run | `LlmSessionActor.RecoverChildren` | durable lifecycle |
| Prompt replay and dedupe (PR 3) | `LlmSessionActor` join handler; `ChannelOutputEngine`; `ChatViewModel`; `HeadlessChannel` | actor-local; binding journal |

### Alternatives rejected

| Alternative | Why rejected |
| --- | --- |
| Keep withdrawing (today) | It breaks the steady state that the owner chose. The operator comes back to withdrawn prompts and lost work after every config reload. The parent model then often starts the child again, and the child repeats its earlier calls. |
| Child journal (D8, P7) | A second persistence system with a purge actor, tombstones, an orphan repair, and SQLite-only proof. The run record is durable already (D2). |
| Park only at a graceful drain | Two resume paths. A crash loses the child. A drain races with running sibling calls. |
| Live wait plus a park record | Two resume paths, and the answer-before-park race of D8.3 (B1) comes back. |
| Freeze the model and the tool set | Needs stored schemas and an exact model lookup, and a stale value needs its own failure rule. The parent session already resolves both again after a restart. |
| Run each child as a full `LlmSessionActor` | It reuses all durable approval code, but it changes the child execution model, the session catalog, and the log partition. The cost is much larger than this issue. A future convergence can start from this design. |
| A conversation file in the run folder | A second store with no atomic link to the run record. |

### Size and cost

The full goal is affordable in this shape. No part needs a new actor type, a new persistence ID, or a new store.

- PR 1 is the largest: the park and resume in `SubAgentActor`, the owner handlers, two events, one lifecycle case, and
  the proto. Estimate 900 to 1,300 changed lines with tests.
- PR 2 is small: recovery, hydration, `AllowsSnapshot`, and passivation. Estimate 300 to 500 lines.
- PR 3 is small: replay and dedupe. Estimate 200 to 350 lines.

If the owner wants less: PR 1 and PR 2 give the whole value for Slack, Mattermost, Discord, and a TUI that stays open.
PR 3 only adds a TUI that starts again and headless output.

### Stack and test-first plan

Three code PRs, stacked on `feature/durable-child-consent-wait`. Each PR starts with failing tests. `tasks.md` lists
each test by name and assertion.

- **PR 1, park and resume while the daemon runs.** A restart still withdraws a parked prompt (`DaemonRestarted`),
  and a session with a parked run does not passivate, so PR 1 ships alone. Forward-only format.
- **PR 2, recovery keeps parked runs.** It changes two PR 1 tests on purpose (restart and passivation).
- **PR 3, prompt replay.** It includes a TUI headless typed-key test and the native smoke run (`run-smoke.sh light`).

Stryker: one new target, `child-resume-gate`, in the `child-consent` group (about 3 minutes; the group goes from 7-8 to
about 11 minutes, under the 25-minute job timeout). The spans: the open-request check and the drain check of
`TryResumeParkedRun`, the order "persist `Resumed`, then spawn", the authority source (`run.OriginalContext`), and the
per-call answer in the resumed `ExecuteToolsAsync` (an approved answer seeds consent only for its call ID; a refused
answer never dispatches). The PR 1 security tests kill these mutants.

## Risks / Trade-offs

- [The session journal and snapshot hold the child conversation while the run is parked.] → Only parked runs write it,
  and `Resumed` drops it from the state. A child conversation is bounded by its model context window. A second park
  writes the whole conversation again. Parks need a person, so they are few. Measure before a delta format.
- [The prompt appears later in a batch with a slow sibling.] → The delay is bounded by that sibling. The pause and the
  digest start when the prompt appears.
- [Forward-only format from PR 1.] → An older binary cannot read `Parked` or `Resumed`. Back up `netclaw.db` before
  rollout. PR 1 writes no new event until a child needs consent.
- [The resumed child gets a new system message.] → Identity and rule files can change between park and resume. The
  parent session has the same behavior after a restart.
- [The turn-state tracker, the token counters, and the managed temporary corrections start empty at resume.] → They
  shape hints and telemetry only. The parent has the same behavior after a restart.
- [A running child is still lost at a restart.] → Out of scope. Park always makes the waiting case, which has no time
  bound, durable. A running child has a bounded run time.
- [Conflict with the open change `child-approval-events-and-parent-pause`.] → Its D8, its PR 3 tasks (7 to 9), and its
  park and purge spec deltas describe the child journal. They must leave that change before it syncs (task 1.1).
  Requirement names in this change differ, so the two syncs do not collide.
- [Conflict with `subagent-explicit-model-selection`.] → If that change records an explicit model on the run, the
  resume reads it from `park.Spawn` in place of the role. The resume rule stays "resolve as a spawn does".
- [Conflict with `stop-repeated-tool-cycles` and `streaming-tool-call-execution`.] → The park point waits for every
  call of the response. Cycle state starts empty at resume.

## Migration Plan

1. Merge this spec PR. The owner confirms the decisions in Open Questions.
2. Remove the D8 material from `child-approval-events-and-parent-pause` with the OpenSpec skills, then sync and archive
   that change after its PR 1 and PR 2 (already merged).
3. PR 1 (forward-only). Back up `netclaw.db` before the rollout.
4. PR 2. Rollback to PR 1 is safe: PR 1 reads every PR 2 record and withdraws parked prompts at a restart.
5. PR 3. Rollback to PR 2 is safe: the two new proto fields are optional.
6. Sync this change and archive it.

## Open Questions

These are owner decisions from 21 October 2026 that this design changes. Each has a recommendation. A "no" changes
the named part only.

1. **P7 (child journal).** Recommendation: replace it with the park in the run record (D2). A "no" restores D8 and
   its purge.
2. **P10 (frozen model).** Recommendation: resolve the model by role at resume, as a spawn does, and fail only when no
   provider is usable (D4). A "no" adds a recorded provider and model and an exact lookup.
3. **Frozen tool set (B3).** Recommendation: resolve the tools under the current policy (D4). A "no" stores each tool
   schema in the park.
4. **B1 (answer before the park).** Recommendation: the prompt appears only after the park persists (D1, D2). The race
   then cannot occur. The cost is a later prompt in a batch with a slow sibling.
5. **A deleted agent profile.** Recommendation: it does not stop a parked run, because the park holds the agent prompt.
   An alternative is to fail the resume with `agent_definition_unavailable`.
