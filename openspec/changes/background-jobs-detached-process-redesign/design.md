# Design: Background Jobs as Detached Processes

## Context

Background jobs today are "shorter-lived shell commands that report when done": `BackgroundJobExecutionActor` spawns the process, drains stdout/stderr to an in-memory bounded window until EOF, awaits exit, writes the log to disk once, and reports `BackgroundJobCompleted`. Three observable consequences:

1. A process that never exits (dev server, watcher) wedges the actor's capture task forever — completion never fires, the job is undead.
2. The output log does not exist on disk until exit, so `check_background_job`'s disk-tail query and the skill-documented `file_read`/`grep` monitoring return nothing mid-run.
3. The pipeline injects a synchronous default timeout at submission, so jobs without an explicit hint are killed early; loud-validation extraction (#1398) normalizes non-positive hints to `null`, so an un-timered job is unreachable by any input.

The redesign makes detachment the model: a background job is a process the daemon supervises but does not wait for. Termination — exit, timeout, cancel, reap, or loss — is an event reported to the owning session.

Constraints: single-process MVP; actor boundaries stay transport-agnostic; persistence types stay framework-owned; default-deny security posture; no silent fallbacks; `TimeProvider` for all time.

## Goals / Non-Goals

**Goals**

- Long-running processes are usable: submit, monitor via live log, interact (curl/Playwright), terminate.
- Mid-run observability through the surfaces that already exist (`check_background_job`, `file_read`, `grep`) — fed, not redesigned.
- Deterministic cleanup: a passivated session leaves no processes behind; a daemon restart accounts for every job loudly.
- No new tool schema, statuses visible to the model kept minimal, no new config knobs.

**Non-Goals**

- Daemon-side readiness detection (regex on output). The agent polls.
- Push-streaming job output into the session as turns.
- Jobs that survive passivation or daemon restart.
- Multi-node ownership (single-process MVP).

## Decisions

### D1: Evolve `background-job-execution`, no new "server mode"

A "server" is not a different kind of job — it is a job whose exit nobody requires. A mode flag (`_server: true` with ready-pattern matching) was considered and rejected: it forks the execution path, adds tool-schema surface the model must learn (skill + eval cost), and the readiness machinery (regex compile, ReDoS bounds, snapshot ticks) duplicates what the agent can do by reading the log it already knows about.

### D2: Stream output to disk; the file is the source of truth

The execution actor's pump tasks append each redacted line to `output.log` as it arrives (flush per line). The in-memory bounded accumulator is removed; the completion tail and `check_background_job`'s tail are read from the file (seek-from-end, not `ReadAllText`). Rotation bounds disk: when `output.log` exceeds ~5 MB it rotates once to `output.1.log` (overwriting any previous), so total disk per job ≤ ~10 MB and the most recent output is always in `output.log`.

- *Alternative considered*: in-memory ring buffer + periodic snapshot messages to the manager. Rejected: duplicates state the disk already holds, adds actor protocol, and dies with the daemon — disk persists output for `Lost` jobs, which reconciliation notifications can point at.
- *Redaction trade-off*: `SecretOutputRedactor` moves from whole-output-at-exit to per-line-at-write. Secrets spanning a line boundary would evade a per-line pass; in practice the redactor's patterns are token-shaped (single-line). This is the price of the log existing while the process runs, and is noted in the spec delta.

### D3: Omitted `_timeout_seconds` = no kill timer

`StartBackgroundJob.TimeoutSeconds` becomes 0 (the actor already treats 0 as "no timer") when the hint is absent. Positive hints arm the existing kill timer; non-positive hints are already normalized to `null` by #1398 extraction with loud notices — no magic sentinel like `0 = unlimited` is exposed to the model. The backstop for forgotten jobs is reap-on-passivation (D4), not a wall-clock default.

- *Alternative considered*: generous default lifetime (e.g. 24h config knob). Rejected by product decision: session passivation is the natural cleanup boundary; a wall-clock default re-introduces "the daemon killed my job at an arbitrary time" semantics and a config knob nobody tunes.

### D4: Reap on passivation, via handshake, without turn delivery

When `LlmSessionActor` enters `Passivating` it sends `KillJobsForSession(sessionId)` to `BackgroundJobManagerActor` and waits for the ack (bounded by a short timeout; on timeout it logs and proceeds — the manager kills idempotently and processes die with the daemon in the worst case) before taking the final snapshot. The manager kills each owned execution child (process tree), marks definitions `Reaped`, and acks.

Reaped jobs do NOT produce `DeliverTrustedSessionTurn`: delivering would rehydrate the session that is passivating — a kill/wake livelock. Instead the session marks its `ActiveJobInfo` entries reaped *before* the final snapshot, so the next rehydration's `[active-background-jobs]` block shows `status: reaped` with the log path; entries are pruned after the next completed turn. The agent learns what happened exactly once, passively.

- *Actor boundary*: session → manager by message (pub/sub-compatible ask), never direct process access; the manager remains the only owner of execution children.
- *Race*: a job may complete while passivation is in flight. `BackgroundJobCompleted` delivery rehydrates the session (existing behavior, kept as a correctness backstop); dedup by job ID prevents double-reporting if the reap marked it first.

### D5: Lost notifications on reconcile

`HandleReconcile` already rewrites `Running`/`Pending` definitions to `Lost`. It now also emits the standard termination delivery (status `Lost`, log path — which, thanks to D2, contains everything the process said before the daemon died) to each owning session. Volume is bounded by D4: only sessions that were warm at crash time can have live jobs.

### D6: Remove the pending-approval passivation deferral

`ToolApprovalRequested`/`ToolApprovalResolved` are journaled; `session-resume` already requires an approval response after idle passivation to rehydrate and resume the original turn, and `HandleToolInteractionResponseWhenIdle` re-drives parked batches from history. The `Ready` receive-timeout deferral on `_pendingToolInteractions.Count > 0` predates approval persistence and now only keeps memory pinned overnight waiting for a button click. Remove it; keep the active-subscriber deferral (live CLI/TUI connections are genuinely ephemeral) and the existing resolved-approvals abandonment path.

The invariant comment near the recovery path ("a passivating session always has an empty `_pendingToolInteractions`") no longer holds and the dependent logic must be re-audited as part of implementation — pending interactions can now be parked across passivation by design.

Ordering note: with D4, a session passivating with a pending approval also reaps its running jobs. Approving later resumes the turn; if the resumed batch needed a job that was reaped, the agent sees the reaped entry and resubmits. Accepted.

## Persistence Implications

- `BackgroundJobStatus` gains `Reaped` (string-serialized in job definition JSON; additive, no migration).
- `ActiveJobInfo` gains a reaped marker surfaced by the context block; protobuf snapshot schema extended with an optional field (wire-compatible, additive).
- No journal event shape changes; the reap mark is folded into existing session-state persistence before the passivation snapshot.
- Job definitions on disk are unchanged in shape; `Reaped`/`Lost` are status values written through the existing store.

## Failure Modes & Recovery

| Failure | Behavior |
|---|---|
| Daemon crashes mid-job | Definition still `Running` on disk → reconcile marks `Lost` → session notified with log path; log contains streamed output up to crash |
| Kill handshake ack times out at passivation | Session logs loudly and proceeds to snapshot; manager kill is idempotent; orphan dies with daemon at the latest |
| Job completes during passivation handshake | Completion delivery rehydrates session (existing path); job-ID dedup prevents double accounting |
| Log write fails mid-stream (disk full) | Pump logs the error and continues draining pipes (child must never deadlock on full pipe); completion reports truncated capture loudly |
| Rotation race with tail query | Query reads `output.log` only; worst case it sees a freshly-rotated short file — acceptable for a monitoring tail |
| Approval response arrives for passivated session | Existing rehydrate-and-resume path (`session-resume`), now exercised routinely instead of only after cold recovery |
| Passivation with BOTH a parked approval and reaped jobs | The final snapshot is skipped (snapshots intentionally exclude parked-approval state), so the snapshot-only reap marks are lost on recovery; the context block may show reaped jobs as running until the agent queries. The manager's on-disk definitions stay authoritative — `check_background_job` returns `Reaped`. Accepted narrow window; a journaled reap event is the follow-up if it bites in practice |

## Migration Plan

Single deploy; no data migration. Behavior changes are immediate:

- Existing agents' omitted-timeout jobs stop dying early (strictly less surprising).
- Jobs no longer survive passivation — AGENTS.md, SKILL.md (version bump), and the runbook ship the new contract in the same PR; eval suite revalidates agent behavior.
- Rollback = revert the PR; `Reaped` status values in job JSON read back as unknown on old code — the store's loud legacy-rejection path already covers unknown statuses.

## Open Questions

None blocking. Rotation threshold (5 MB) and handshake ack timeout (~5s) are implementation constants, tunable later if operations demand it.
