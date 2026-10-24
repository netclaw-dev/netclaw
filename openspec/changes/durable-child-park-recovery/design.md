## Context

See `proposal.md` (Why). Terms follow [the engineering glossary](../../../docs/spec/GLOSSARY.md): consent request,
requester, parked child run, child consent pause. This design is PR 2 of `durable-child-consent-wait`. That change
holds designs D1 to D7 and the requirement "Recovery keeps parked child runs". This design records only what PR 2
adds or decides differently.

Current state on `feature/durable-child-pr1` (rebased on `dev` after #2651):

1. The park is actor-local in `LlmSessionActor` (`_parks`, `_promptsIssued`). A restart loses it, and recovery
   records the run as lost (`owner_restarted`).
2. The owner writes the child requests of one park with one `PersistAll` of `ToolApprovalRequested`.
3. The answer of a parked request updates only `SessionToolWorkflow`, not the session state.
4. `ChildRunPark` holds `Transcript`, `LoopGuard`, `ScopeDirectories`, `FileActivity`, `SkillOverlay`, and `Requests`.
5. A rejected child event ends a run only while `_parks` holds the run (the PR 1 race guard).
6. #2651: the restart drain journals one text-free continuation `InputAdmitted` for a turn that stopped after its
   tool batch. `PersistAdmission` closes a stale continuation for an ordinary admission. `AdmitConsentNoticeThen`
   (through `PersistReplacingInput`) and `AdmissionDecision.ReplaceWaitingFire` do not close it.

## Goals / Non-Goals

**Goals:**

- One durable copy of a park: the run record in `SessionState`. No actor-local park.
- Each writer check mirrors its reducer guard, so the writer never writes an event that a replay refuses.
- One recovery path for the journal and the snapshot.
- The #2651 continuation close on every admission path.

**Non-Goals:**

- A running child at a restart (#2655). Prompt replay. `DeferredMedia`, field 25 (`platform_temporary_root`), the
  `check_agent_run` "parked" state, and M6.
- The recovery cost test (task 7.11 of `durable-child-consent-wait` is cut). The cap and the size log bound the cost.

## Decisions

### D1. Formats

| Message | Change | Field |
| --- | --- | --- |
| `ChildRunEventV2Proto.fact` | `ChildRunParkProto parked` | 8 |
| `ChildRunEventV2Proto.fact` | `bool resumed` | 9 |
| `BackgroundChildRunV2Proto.lifecycle` | `ChildRunParkProto parked` | 21 |
| `BackgroundChildRunV2Proto` | `int32 prompts_issued` (not in the oneof) | 22 |
| new `ChildRunParkProto` | `transcript` (`SerializableChatMessageProto`), `loop_guard`, `project_directory`, `inherited_working_directory`, `file_activity` (`ChildWorkingContextProto`), `skill_overlay`, `requests` (`ChildRunApprovalProto`) | new |
| new `ChildLoopGuardProto` | the fields of `TurnLoopGuardState` | new |

- Reuse `SerializableChatMessageProto`, `ChildWorkingContextProto`, and `ChildRunApprovalProto` (request plus
  resolution). No new manifest, no new persistence ID, and no new snapshot field. No field 25.
- New reason: `park_damaged`. (`park_too_large` comes from the PR 1 design.) A reason is a free string on the wire.
- A rejected `Resumed` reuses `tool_execution_failed` with "The parked calls did not run." Alternative: a new
  `resume_not_recorded` reason. Rejected (owner, 23 October 2026): PR 1 cut `park_not_recorded` the same way.

### D2. Domain

- `ChildRunEvent.Parked(ChildRunPark Park, IReadOnlyList<ToolApprovalRequested> Requests)` and
  `ChildRunEvent.Resumed`.
- `ChildRunLifecycle.Parked(ChildRunPark Park, ImmutableArray<ChildParkRequest> Requests)`, with
  `ChildParkRequest(ToolApprovalRequested Request, ToolApprovalResolved? Resolution)`.
- `BackgroundChildRun.PromptsIssued` replaces `_promptsIssued`. A second park of one run never reuses a call ID that a
  channel binding still tracks.
- `ChildRunPark` loses `Requests`. `ChildParkReached` carries the `ParentApprovalRequest` list beside the park. The
  resumed child takes its parked call IDs from the keys of `ChildRunResume.Answers`.
- `_parks` goes away. `_state.ChildRuns` is the only copy.

### D3. Reducer (`SessionState.Apply`)

| From | Event | To |
| --- | --- | --- |
| `Accepted` | `Parked` | `Parked(park, requests with no resolution)`; `PromptsIssued` grows by the request count |
| `Parked` | `ToolApprovalResolved` of one of its calls | the same run, with that resolution |
| `Parked` | `Resumed` | `Accepted` with a checkpoint: round = `park.LoopGuard.ToolIterationCount`, activity = `park.FileActivity`, summary "The child resumed after consent." |
| `Parked` | `TerminalRecorded` | `Terminal` |
| another run | `ToolApprovalResolved` with `SourceChildRunId` | no change (a 0.28 live-wait answer replays this way) |

Caution: a reducer throw during replay stops the session. Each writer check mirrors its reducer guard.

### D4. Journal, snapshot, and the size cap

- One `Persist(Parked)` replaces the PR 1 `PersistAll` of requests. One park stays one atomic write. PR 2 writes no
  separate child `ToolApprovalRequested`.
- The answer goes into the park in the persist callback (PR 1 deferred item, task 4.5 of `durable-child-consent-wait`).
- `AllowsSnapshot` ignores child entries (`ChildParkRules.IgnoredBySnapshot`). Open parent entries still block it.
- After `Resumed`, the state holds no park. The next snapshot and its journal delete remove the `Parked` row.
- `ChildRunPark.MaxBytes = 4 MiB`. The owner measures `NetclawProtoMapper.ToProto(parked).CalculateSize()` before it
  persists (`ChildParkRules.ParkFits`). Above the cap: `RecordChildTerminal(Failed, park_too_large)`, one Error log
  with the run ID and the size, and no prompt. The owner logs the size of each accepted park at Info. Recovery logs
  `child_parks_recovered count={n} bytes={b}` only when n > 0. No line holds content.

### D5. Owners and data

| Decision | Owner | Data |
| --- | --- | --- |
| Accept a park, the cap, prompt numbers | `LlmSessionActor.AcceptChildPark` | durable `Parked`; the size is call-local |
| Put the answer into the park | `SessionState.Apply(ToolApprovalResolved)` | durable |
| Resume gate | `ChildParkRules.ResumeGate` (PR 1) | call-local |
| Damaged-park rule | `ChildParkRules.LoopGuardDamage`, called by `PrepareResume` | call-local |
| Resume record | `TryResumeParkedRun` | durable `Resumed` |
| Recovery choice for each run | `ChildParkRules.RecoveryAction`, called by `RecoverChildren` | durable lifecycle |
| Open workflow entries, the pause | `LlmSessionActor.RestoreParkedRequests` (recovery) and `ApprovalRequestCommitted` (live) | actor-local, rebuilt from the run record |
| Restart reminder and passivation count | `ChildParkRules.CountsAsUnreported` | call-local over durable state |
| End on a rejected child event | `ChildParkRules.RejectedEventEnds(kind, lifecycle)` | call-local |
| Snapshot rule for child entries | `ChildParkRules.IgnoredBySnapshot`, called by `AllowsSnapshot` | call-local |
| Continuation close at admission | `LlmSessionActor.PersistReplacingInput` (one helper for every admission path) | durable `InputClosed` in the same write |

`RejectedEventEnds` replaces the PR 1 check `_parks.ContainsKey`. A rejected event ends the run only when the run still
has the lifecycle that the event expected: `Parked` expects `Accepted`; an answer and `Resumed` expect `Parked`.

### D6. Flows (schematic: they omit the requester check of `ValidateResponse` and the tool gate, which do not change)

```text
F1 park (AcceptChildPark)
  decision := ChildParkRules.AcceptPark(owned, run.Lifecycle)            # PR 1
  requests := ForChildCall(..) each; number from run.PromptsIssued
  evt := Parked(park, requests)
  if !ParkFits(size(evt)): end Failed park_too_large; return
  release the child                                                       # PR 1
  Persist(evt): Apply; ApprovalRequestCommitted for each request (entry, UntilClosed prompt, digest, pause)

F2 answer (live or after a restart)
  Persist ToolApprovalResolved: Apply (answer into the park); entry; ack; close prompt; digest
  TryResumeParkedRun: ResumeGate -> PrepareResume (LoopGuardDamage -> park_damaged)
    Persist Resumed: Apply (Parked -> Accepted round N); StartChildActor(name-{PromptsIssued})

F3 recovery
  replay or the snapshot offer rebuild SessionState only
  withdraw the child entries that replay left open (runs that did not park)
  RestoreParkedRequests: open the entries of each Parked run from its run record
  if the pause holds: queue the held inputs before the stashed commands
  log child_parks_recovered (n > 0 only)
  RecoverChildren: RecoveryAction(lifecycle), chosen at RecoveryCompleted
    Accepted or Cancelling -> lost, owner_restarted                      # unchanged
    Parked                 -> TryResumeParkedRun (the resume gate waits while a request is open)
    Terminal               -> prepare the result                        # unchanged

F4 admission with a pending continuation (#2651 contract)
  stale := the pending continuation that no turn selected
           (a continuation that the pause holds is kept by the approval notice and a child result notice;
            a person input or a fire closes it)
  every admission path calls PersistReplacingInput(waiting?, stale?, admitted)
    one write: InputClosed(waiting and stale) + InputAdmitted
    stale closed -> post the restart notice once
```

- F5 cancel: `ControlChild` checks `run.Lifecycle is Parked` in place of `_parks.Remove`.
- F8 passivation: `HasUnreportedChildren` drops `_parks.Count > 0`. A parked run with an open request does not keep the
  session in memory. A parked run with all answers does (it waits only for a drain to end). The idle rule "abandon a
  parked tool batch with resolved approvals" ignores child entries.
- F9 drain: `UnreportedRuns()` leaves out a parked run with an open request (`CountsAsUnreported`). The #2651 builder
  and its authority order do not change. N1: while `IsChildConsentPaused`, `TryResumeInterruptedInput` leaves out each
  input that the pause holds, and it acknowledges with no turn when no other input remains. A continuation of an
  automation turn is such an input. It runs once through the held-input path after the last answer.
- F4 decision: the lost-child notice (`owner_restarted`) already goes through `PersistAdmission`, so it closes the
  continuation today. PR 2 pins it with a row. The two missing paths (the approval notice and `ReplaceWaitingFire`)
  get the close through the shared `PersistReplacingInput`. Alternative: a separate close command after each
  admission. Rejected: two writes let a crash keep the continuation and the newer input, and the stopped turn could
  then run after a later input.

### D7. Boundaries with examples

| Boundary | Positive example | Negative example |
| --- | --- | --- |
| Recovery keeps a parked prompt | Alice's child parks on `gh pr merge 42`. A config reload restarts the daemon. Alice clicks "Approve once" on the old prompt. The call runs once. | Bob clicks the same prompt after the restart. `approval_wrong_requester`. Nothing persists. Nothing runs. |
| At most once across a crash | A crash after the last answer and before `Resumed`. Recovery resumes, and the call runs once. | A crash after `Resumed`. Recovery records the run as lost. The call does not run again. |
| The size cap | A park of 300 KB persists. | A park of 5 MiB ends `park_too_large`. No prompt shows. |
| The damaged-park rule | A valid loop guard resumes. | `EqualRounds = 7` ends `park_damaged` at the answer. Recovery completes. |
| Passivation | A session with one parked open run passivates. A click wakes it and resumes the call. | A parked run whose answers all arrived during a drain keeps the session in memory until the restart. |
| Snapshot rule | A routine snapshot runs while a child request is open. | A routine snapshot does not run while a parent approval is open. |
| Continuation close | An approval notice after the restart closes Alice's continuation in its own write, and Alice gets the restart notice once. | The notice is admitted, the continuation stays, and the restart reminder later runs Alice's stopped turn after newer input. |

## Risks / Trade-offs

- [Forward-only format] → A rollback after a park row needs the verified backup. Ship first in a prerelease. The swap
  rule adds `PARK-EVENT` (a `cre-v2` row with field 8 or 9) and `PARK-SNAPSHOT` (an `ss-v2` entry with field 21), with
  `RESTORE_KINDS="PARK-EVENT PARK-SNAPSHOT"`. The first swap runs one canary: park a child on a shell command, swap,
  click "Approve once", and check that the command runs once.
- [Snapshot size with many parks] → Keep the 4 MiB cap and the size log. Decide on a session total later, with data.
- [A late click after a resume and a snapshot gets the closed-call text] → Accepted (owner). Nothing runs either way.
- [Today's lost runs were running children] → Out of scope (#2655). PR 2 recovery already resumes a park with no open
  request, so the follow-up adds only a drain signal and the child stop.

## Migration Plan

- Upgrade from 0.28.0-beta.2: safe. A child request row of a beta.2 live wait belongs to an `Accepted` run. Recovery
  withdraws it with `DaemonRestarted`.
- Downgrade: 0.28.0-beta.2 reads fact 8 or 9 as `FactCase.None` and lifecycle 21 as `LifecycleCase.None`. The mapper
  throws, and recovery of that session fails. It ignores field 22 and accepts the new reason string. PR 1 alone has
  the same mapper.
- Release-note text (Upgrade impact): "A child run that waits for consent now survives a restart, a config reload, and
  passivation. Netclaw stores the waiting child in the session journal. An older Netclaw binary cannot recover a
  session that holds such a child. Before you install an older binary, back up `netclaw.db`, and restore that backup
  with the older binary."
- Merge: PR 2 targets `feature/durable-child-pr1`. Merge from the bottom up in one sitting. Cut no release tag between
  the two merges.
