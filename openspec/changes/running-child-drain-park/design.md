## Context

See `proposal.md` (Why). Terms follow [the engineering glossary](../../../docs/spec/GLOSSARY.md): parked child run,
child consent pause, consent request, requester. This change builds on `durable-child-park-recovery` (PR 2, #2670).
That design holds the park record (D1, D2), the reducer (D3), the cap (D4), the owners (D5), and the recovery flow (F3).
This design records only what the drain park adds.

Current state on `feature/durable-child-pr2` (a383625d9):

1. `PrepareForDaemonRestart(SessionId, Reason)` reaches the session. The session stops its turn and passivates.
   `FinalizePassivation` acknowledges at once. `PostStop` stops each live child. The run stays `Accepted`, and
   recovery records it as lost (`owner_restarted`).
2. The daemon-stop drain uses `DaemonConfig.BoundedDrainTimeout` (20 s, static in `Netclaw.Configuration`, which
   `SessionExecutionLifetime` already imports). The config-reload drain uses its own copy,
   `DaemonRestartCoordinator.RestartDrainTimeout` (20 s). A session that does not acknowledge before the deadline gets
   no restart reminder.
3. A child parks only from `ToolExecutionCompleted`, with one or more consent requests.
4. The reducer accepts `Accepted + Parked` only `when parked.Requests.Count > 0`.
5. `AcceptChildPark` ends the run when the requester has no interactive approval, for every park.
6. `SubAgentActor.ResumeFromPark` reads the parked calls from the last assistant message and runs them as a tool batch.

These PR 2 rules already hold for a park with zero requests, with no change:

| Rule | Existing row | Result for a drain park |
| --- | --- | --- |
| `ChildParkRules.ResumeGate` | `(park, 0 open, drain)` → `Wait`; `(park, 0 open, no drain)` → `Resume` | waits during the drain, resumes after it |
| `ChildParkRules.RecoveryAction` | `Parked` → `Resume` | recovery tries the resume |
| `ChildParkRules.CountsAsUnreported` | `Parked`, 0 open → true | the run makes the restart reminder |
| `ChildParkRules.AcceptPark` | `Cancelling` → `EndCancelled` | a cancel that comes first wins |
| `TakeParkedAnswers` | no entry → empty map | the resume has no answer |
| `IsChildConsentPaused` | true only with an open child request | a drain park does not pause the session |
| `Resumed` reducer guard | every request has an answer | true for zero requests |
| Mapper `ReadParkRequests` | an empty list is valid | no format change |

## Goals / Non-Goals

**Goals:**

- One park record, one reducer, one recovery path, and one resume gate for a consent park and a drain park.
- No call runs twice. The resumed child keeps `run.OriginalContext`.
- The session keeps its restart reminder when a child misses the wait.

**Non-Goals:**

- A grace period for the model call of a child (see D2).
- A round or a tool-call limit (#2656).
- An expiry for a drain park (see Open decision A3).
- A new journal format.

## Decisions

### D1. The rules that cannot express a drain park, and the smallest change for each

| # | Rule today | Why it fails for zero requests | Change |
| --- | --- | --- | --- |
| R1 | Reducer: `Accepted + Parked when Requests.Count > 0` | It throws, and a replay that throws stops the session. | Drop the count guard. Keep `Requests.All(SourceChildRunId == run.RunId)`. |
| R2 | `AcceptChildPark` ends the run when `!SupportsInteractiveApproval` | A drain park asks nobody. The rule would fail every drain park of an automation turn. | New pure rule `ChildParkRules.RequesterCanAnswer(requestCount, supportsInteractiveApproval) => requestCount == 0 \|\| supportsInteractiveApproval`, inside the `child-resume-gate` span. A damaged authority still ends the run. |
| R3 | `ResumeFromPark` runs the parked calls as a tool batch | With no answer, `_history.Last(assistant)` throws when the child parked before its first model reply. `RunTools([])` calls `RecordToolCompletion`, which adds a false round. | New pure rule `ChildParkRules.ResumeStart(parkRequests, answers)`: 0 and 0 → model call; requests > 0 and answers equal to requests → parked calls; any other pair → the run ends. The owner applies it in `TryResumeParkedRun` (end: `EndParkedRun`). The child applies it to the unanswered calls of the park, and a mismatch ends the run as `park_damaged`. |
| R4 | Reducer `Resumed` summary: "The child resumed after consent." | The text is false for a drain park. `check_agent_run` and a later loss notice show it. | Pick the text by the request count of the park: zero → "The child resumed after the restart." |
| R5 | Nothing tells a live child to stop, and the acknowledgement does not wait | The drain stops the session, and `PostStop` kills the child. | D2, D3, D4. |
| R6 | `EndParkedRun` ends a refused park as failed with "The parked calls did not run." | A drain park asked for no consent, and its batch already ran, so the text is false. | A refused drain park (too large, damaged authority, a failed resume, a rejected record) ends like a lost run: `Partial` with `owner_restarted`, the file activity of the park, and the restart-loss text. The refusal reason goes to the warning. A consent park keeps today's text. |

A park with zero requests writes `PromptsIssued + 0`. The resume actor name `{name}-{PromptsIssued}` can repeat the
name of an earlier incarnation. That is safe: a drain park resumes only in a new session incarnation (after the
restart), and the children of the old incarnation stopped with it.

### D2. The child stop point

The child gets `ParkForDrain` (actor-local message, no serialization). After it, the child starts no model call and no
tool batch.

- A tool batch runs: the child sets `_parkForDrain`. The batch ends as today. A parked call makes a consent park
  (precedence). With no parked call, the child records the results and calls `FireLlmCall`. `FireLlmCall` checks
  `_parkForDrain` first, before the checkpoint, and parks.
- No tool batch runs (a model call waits, or a checkpoint commit waits): the child parks at once. `EndRun` already
  cancels `_executionCts`, so the model call stops. Its response never enters the transcript. A late
  `ChildCheckpointReached` of a released child gets the existing failure reply, and a late commit is harmless.
- The child needs one bool for "a tool batch runs" (set in `RunTools`, cleared by `ToolExecutionCompleted` and
  `ChildToolExecutionFailed`).
- The drain park uses `Park([], directories)`. The directories are the current `RunScope` values, so the resume
  applies no directory change.

Alternative: a 2-second model grace, as the parent has. Rejected: a timer in the child for a small gain. A model call
has no outside effect, and the resumed child makes the call again.

Alternative: reply "park" to `ChildCheckpointReached`. Rejected: it covers only the checkpoint state, and the order of
the Ask reply and other messages is not fixed.

### D3. The child wait

Reuse before you add: the session reads the existing `DaemonConfig.BoundedDrainTimeout`. The child wait is
`SessionExecutionLifetime.ChildWait = DaemonConfig.BoundedDrainTimeout - ChildWaitMargin`, with
`ChildWaitMargin = 2 s`, beside `RestartModelGracePeriod`. The default of `DaemonRestartCoordinator.RestartDrainTimeout`
becomes `DaemonConfig.BoundedDrainTimeout`, so both drains share one value. `PrepareForDaemonRestart` does not change.
The session acknowledges before the caller stops waiting, so it keeps its restart reminder. Session tests drive the
wait with the existing `TestScheduler`.

Alternative: a required `ChildWait` field on `PrepareForDaemonRestart`. Rejected: it duplicates a value that the session
can already read, and it touches about 35 call sites.

Alternative: no bound in the session. Rejected: a session that misses the caller deadline gets no restart reminder,
so the notice of a lost child and the continuation of the parent turn (#2632) wait for a later message.

### D4. The acknowledgement gate

- `SessionExecutionLifetime.RequestRestart` sends `ParkForDrain` to each live child and starts the
  timer `RestartChildWaitExpired` after `ChildWait`, only when a live child exists.
- `FinalizePassivation` starts with
  `if (SessionExecutionLifetime.WaitsForChildren(RestartDrainRequested, HasLiveChildren, childWaitEnded)) return;`.
  `WaitsForChildren` is pure: `drain && liveChildren && !waitEnded`.
- `ContinueDrainAfterChild()` calls `FinalizePassivation()` when the phase is `Passivating`. It runs in the journal
  callback of `Parked` (`AcceptChildPark`) and of `TerminalRecorded` (`RecordChildTerminal`). It never runs before the
  callback: `AcceptChildPark` releases the child before `Persist`, and an acknowledgement before the write commits can
  lose the park.
- `RestartChildWaitExpired` sets `childWaitEnded`, logs one Warning with the live child count
  (`Restart drain child_wait_ended live_children={count}`), and calls
  `FinalizePassivation()` when the phase is `Passivating`. It is handled in every phase.
- `ClaimFinalStop` already makes the gate idempotent: a second call after the claim does nothing.

### D5. Owners and data

| Decision | Owner | Data |
| --- | --- | --- |
| The child wait | `SessionExecutionLifetime.ChildWait` (from `DaemonConfig.BoundedDrainTimeout`) | static |
| Signal each live child, start the wait timer | `SessionExecutionLifetime.RequestRestart` | actor-local |
| Stop point of the child | `SubAgentActor` (`ParkForDrain` handler, `FireLlmCall`) | child-local |
| Accept a park | `ChildParkRules.AcceptPark` (unchanged) | call-local |
| The requester rule | `ChildParkRules.RequesterCanAnswer` (new) | call-local |
| First step of a resume (model call, parked calls, or end) | `ChildParkRules.ResumeStart` (new), by the request count of the park; the owner and the child apply it | call-local |
| Record a park | `SessionState.Apply(Parked)` | durable |
| The acknowledgement gate | `SessionExecutionLifetime.WaitsForChildren`, called by `FinalizePassivation` | actor-local |
| Recovery choice, resume gate, resume record | PR 2 (unchanged) | durable lifecycle; call-local gate |
| First step of a resumed child | `SubAgentActor.ResumeFromPark` (applies `ResumeStart` to the unanswered calls of the park) | child-local |
| Resume summary text | `SessionState.Apply(Resumed)` | durable, derived |

The Stryker gate `child-resume-gate` mutates two spans: `// <child-resume-gate>` in `ChildRunPark.cs`
(`RequesterCanAnswer` and `ResumeStart` with the PR 2 rules) and `// <child-drain-wait>` in
`SessionExecutionLifetime.cs` (`WaitsForChildren`). `ChildParkRulesTests` holds the tables of both.

### D6. Flows

F1 drain (schematic: it omits the turn stop of the parent, which does not change):

```text
RequestRestartDrain(request)                                     # LlmSessionActor
  _execution.RequestRestart(...)
    for each live child: child.Tell(ParkForDrain)
    if any live child: start timer RestartChildWaitExpired(ChildWait)   # BoundedDrainTimeout - 2 s
  existing: Ready -> Passivating; Processing -> model grace or tool stop

SubAgentActor on ParkForDrain
  _parkForDrain := true
  if no tool batch runs: Park([], current directories)           # EndRun cancels a model call
SubAgentActor on ToolExecutionCompleted                          # unchanged
  parked calls -> Park(requests, batch directories)              # consent park wins
  else record results; FireLlmCall()
SubAgentActor FireLlmCall
  if _parkForDrain: Park([], current directories); return        # before the checkpoint

AcceptChildPark(park, requests)                                  # LlmSessionActor
  AcceptPark(owned, lifecycle)                                   # unchanged
  parse run.OriginalContext, or end failed
  if !RequesterCanAnswer(requests.Count, authority.SupportsInteractiveApproval): end failed
  Parked(park, requests); ParkFits; release the child            # unchanged
  Persist: Apply; ApprovalRequestCommitted for each request; ContinueDrainAfterChild()

FinalizePassivation
  if WaitsForChildren(drain, liveChildren, waitEnded): return
  ClaimFinalStop -> reminder from pending inputs, else from UnreportedRuns -> ack -> stop

RestartChildWaitExpired
  waitEnded := true; Warning(live child count); if Passivating: FinalizePassivation()
  PostStop stops the live children; their runs stay Accepted
```

F2 resume after the restart (all PR 2 steps except the marked ones):

```text
replay or snapshot: Parked(park, []) -> lifecycle Parked with no request   # R1
RestoreParkedRequests: opens nothing; child_parks_recovered counts the park
RecoverChildren: RecoveryAction(Parked) = Resume
TryResumeParkedRun: ResumeGate(park, 0 open, no drain) = Resume
  answers := TakeParkedAnswers(run) = {}
  ResumeStart(0, 0) = ModelCall, else EndRun -> EndParkedRun (R6)            # R3
  PrepareResume: LoopGuardDamage; authority from run.OriginalContext only
  Persist Resumed: Apply -> Accepted(round N, "The child resumed after the restart.")   # R4
                   StartChildActor({name}-{PromptsIssued})
SubAgentActor.ResumeFromPark: transcript, loop guard, round N, file activity
  ResumeStart(0 requests, 0 answers) = ModelCall: FireLlmCall()             # R3
  else: run the parked calls once with their answers (unchanged)
```

### D7. Boundaries with examples

| Boundary | Positive example | Negative example |
| --- | --- | --- |
| Stop point | Alice's child ends its `gh pr view` batch during the drain. It parks with the result. After the restart it makes its next model call. | The child gets the signal while `npm test` runs. It does not start a model call after the test ends, and it never starts a new batch. |
| Model call | The child waits for its first model reply. It cancels the call and parks with the task message only. | The reply arrives after the park. The child ignores it, and the transcript does not hold it. |
| Requester rule (R2) | A reminder turn's child, with no interactive approval, makes a drain park and resumes. | The same child reaches a consent park. The run ends as failed, and no prompt shows. |
| Child wait | A child parks at 3 s. The session acknowledges at once after the park record commits. | A child tool runs past 18 s. The session logs one Warning and acknowledges with the reminder. Recovery records the run as lost, and the tool does not run again. |
| Authority | Bob writes after the restart, and his turn is open when Alice's child resumes. The resumed child's first model call and each later tool call run under Alice's recorded audience, boundary, and delivery targets. | Bob's turn authority reaches the resumed child. This must not happen; the resume reads only `run.OriginalContext`. |
| At most once | A crash after the last answer of a consent park and before `Resumed`: recovery resumes. | A crash after `Resumed` of a drain park: recovery records the run as lost and starts no child. |

## Risks / Trade-offs

- [A drain can take up to 18 s more] → Bounded by D3. Before this change the session did not wait. The shutdown
  budget does not change.
- [A child parks just after the wait ends] → The session stopped, so the park write does not happen. The run is lost,
  as today. At most once holds.
- [Forward-only from 0.28.0-beta.3] → beta.3 ships PR 2 without this change. A beta.3 binary throws on a journal row
  of a drain park (R1), so recovery of that session fails loudly. From a snapshot, the beta.3 mapper reads the park,
  and the beta.3 child crashes in `ResumeFromPark` (R3). No call runs twice in either case. The release that ships this
  change is forward-only from beta.3, and a rollback to beta.3 uses the verified backup. The swap rule kinds
  `PARK-EVENT` and `PARK-SNAPSHOT` of PR 2 already cover the rows.
- [The 2 s margin starts late] → The margin starts when the session gets `PrepareForDaemonRestart`, not when the
  caller deadline starts. A message that reaches the session late can make the acknowledgement miss the caller
  deadline. The session then gives no restart reminder, and the drain park resumes at the next wake of the session.
- [A drain park waits a long time] → After a daemon stop longer than the 10-minute reminder lifetime, the run resumes
  at the next wake of the session (Open decision A3).

## Migration Plan

- No new format. The upgrade from PR 2 is safe.
- Downgrade to 0.28.0-beta.3: restore the verified backup (see Risks). The release notes of the release that ships
  this change say that a drain park needs the backup for a rollback to beta.3.
- Merge: this branch targets `dev` after PR 2 merges. Verify on the combined stack.

## Open decisions for the owner

- A1. Model call in flight: cancel at once (chosen, D2) or a 2-second grace. Recommendation: cancel at once.
- A2. Child wait: `DaemonConfig.BoundedDrainTimeout` minus 2 s, read by the session (chosen, D3). Recommendation: keep.
- A3. Expiry of a drain park: none (chosen). Recommendation: no expiry, as a consent park.
- A4. Release order: decided. 0.28.0-beta.3 ships PR 2 alone, and this change is forward-only from beta.3 (see Risks).
