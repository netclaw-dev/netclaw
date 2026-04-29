## Context

Netclaw's tool execution pipeline is synchronous: the LLM issues a tool call,
the pipeline executes it, and the result is returned in the same turn. For
commands that take minutes to hours (full test suites, large builds, headless
Claude Code sessions), this blocks the session actor — which may idle-timeout,
cannot serve other interactions, and holds resources doing nothing.

The reminder system already solved the hard problem of re-entering a
potentially passivated session with new information. `DeliverTrustedSessionTurn`
delivers a new turn via the gateway, the session rehydrates from Akka
Persistence if passivated, and delivery observation confirms the result
reached the session. Background jobs reuse this re-entrancy channel.

The `structured-tool-call-metadata` change (prerequisite) provides the
`ToolCallMeta` envelope with explicit `_background` signaling and
`_timeout_seconds` synchronous timeout hints. This change implements the
infrastructure that consumes the explicit background signal.

### Current state

- `ShellTool` spawns processes synchronously, enforces a fixed 60s timeout,
  and returns output inline.
- `SessionToolExecutionPipeline` enforces a 90s batch timeout.
- `ReminderManagerActor` is an infrastructure singleton that manages reminder
  lifecycle, spawns `ReminderExecutionActor` children, and uses
  `DeliverTrustedSessionTurn` for session re-entry.
- `SessionState.ProcessedReminderIds` provides dedup for reminder deliveries.

## Goals / Non-Goals

**Goals:**

- Long-running shell commands execute outside the session actor's lifecycle.
- Session can passivate freely while jobs run; results delivered on completion.
- LLM can check job status and cancel running jobs.
- Session tracks active jobs in persisted state for compaction/resumption
  awareness.
- Job definitions/history persist across daemon restarts for reconciliation and
  diagnostics; execution continuity across daemon restart is best-effort only.
- Concurrency limited to prevent resource exhaustion.

**Non-Goals:**

- Background execution for non-shell tools (MCP tools, file operations). Shell
  only for now. The routing infrastructure supports future extension.
- Job scheduling or recurring jobs (that's what reminders are for).
- Streaming output back to the session in real-time during execution.
- Cross-session job sharing (a job belongs to exactly one session).

## Decisions

### D1: BackgroundJobManagerActor as infrastructure singleton

**Choice**: A new `BackgroundJobManagerActor` singleton, sibling to
`ReminderManagerActor`, registered in the actor system at daemon startup.

**Alternatives considered**:

- *Session-owned child actor*: Would die when the session passivates. A
  75-minute job cannot be owned by an actor with a 30-minute idle timeout.
- *Extend ReminderManagerActor*: Jobs and reminders have different triggers
  (process exit vs timer) and different lifecycles. Merging would violate
  single responsibility and complicate both.

**Rationale**: Jobs must outlive sessions. Infrastructure-level singleton is
the same pattern as reminders, proven in production.

### D2: Explicit-only background routing

**Choice**: Background execution is triggered only by `_background: true`.
`_timeout_seconds` remains a synchronous timeout hint and does not change the
execution mode.

The routing decision SHALL happen only after the normal session-owned tool
execution path has finished ACL evaluation and any required approval flow for
`shell_execute`. `BackgroundJobManagerActor` is a post-approval execution
target, not a bypass around session ACL or approval gates. Denied or timed-out
approval results stop in the session pipeline and create no `StartBackgroundJob`
message or persisted job record.

**Rationale**: Waiting longer and switching to deferred execution are different
user intents. Keeping background execution explicit avoids surprising behavior
where a large timeout request silently changes approval, result delivery, and
follow-up semantics.

### D3: Re-entrancy via DeliverTrustedSessionTurn

**Choice**: On job completion, `BackgroundJobExecutionActor` delivers the
result to the originating session via `DeliverTrustedSessionTurn` through
the gateway — the same pathway reminder Mode B delivery uses. This trusted
delivery is intentional parity with normal synchronous shell tool results,
not an accidental trust escalation.

**Alternatives considered**:

- *Direct Tell to session actor*: Would bypass the gateway, skip delivery
  observation, and not work if the session has passivated (no actor to Tell).
- *New notification channel*: Unnecessary when the reminder re-entrancy
  channel already handles passivation, dedup, and delivery observation.

**Rationale**: Background job completion is the deferred completion of a tool
call the session already initiated and approved. Matching the trust level of
normal synchronous shell results preserves behavioral parity: the same shell
execution outcome is delivered as trusted whether it finishes inline or later.
That trust is narrowly scoped to the originating session and the persisted
originating audience/boundary captured at job start. Proven path. Handles
session passivation, rehydration, dedup, and delivery observation without new
infrastructure.

### D4: Session tracks ActiveBackgroundJobs in persisted state

**Choice**: `SessionState` gains `ImmutableDictionary<string, ActiveJobInfo>`
persisted to the Akka journal. `ActiveJobInfo` carries `JobId`, `Command`,
`Rationale`, `StartedAt`. Added on job start, removed on result delivery.
Surfaced in working context so the LLM knows what's pending.

**Rationale**: After compaction or session resumption, the LLM needs to know
it has pending jobs. The rationale from `ToolCallMeta` is the only thing
telling the resumed agent why a job exists and what to do with the result.
Without persisted tracking, the LLM wakes up to random test output with no
context.

### D5: check_background_job with cancel support, coupled to shell availability

**Choice**: Single tool `check_background_job` handles status query and
cancellation (via `Cancel: true` parameter). It is exposed only when shell
execution is available and uses the `shell` grant category. Job lookup, status
read, and cancellation are same-session-only: the manager checks the caller's
session identity plus the persisted originating audience/boundary captured at
job start before returning any job state.

**Alternatives considered**:

- *Separate cancel_background_job tool*: Adds a tool to the surface for a
  single-parameter difference.
- *Independent "jobs" tool surface*: splits discovery and grants away from the
  shell capability even though background jobs are currently shell-only.

**Rationale**: One tool, minimal surface area. Coupling the tool to shell
availability preserves the fact that background jobs are just deferred shell
execution, not an independent capability. Shell grant means it inherits
Personal-only audience restriction automatically. Same-session lookup prevents
cross-session job enumeration. Returning the same generic "job not found"
result for unknown IDs and for session/audience/boundary mismatches avoids
turning the tool into an existence oracle for another session's jobs.

### D6: Job definitions persist to disk as JSON

**Choice**: Job definitions stored at `~/.netclaw/jobs/{id}.json`, same
pattern as reminder definitions (`~/.netclaw/reminders/{id}.json`).

**Rationale**: The system needs enough persisted state to reconcile incomplete
jobs after daemon restart and to support diagnostics. File-per-job is simple,
human-inspectable, and matches the established reminder pattern. On startup,
the manager makes a best-effort reconciliation pass over persisted job state.
This is not a guarantee that in-flight execution survives daemon restart.

### D7: Concurrency limit with queueing

**Choice**: Maximum 5 concurrent background jobs (configurable). Overflow
queued in a FIFO deferred queue, dispatched as capacity becomes available.

**Rationale**: Prevents resource exhaustion from runaway job creation. Same
pattern as `ReminderManagerActor`'s concurrency gating (max 3 concurrent
reminder executions).

## Risks / Trade-offs

**[Risk] Orphaned processes on daemon restart** → On startup, the job manager
reads persisted definitions and reconciles any incomplete jobs best-effort. If
the daemon restarted or went down mid-job, the in-flight job may be lost, may
be marked failed/lost with unknown exit, and the user or agent may need to
relaunch it. Mitigation is best-effort — a clean daemon shutdown should kill
child processes.

**[Risk] Output file disk usage** → Long-running commands can produce large
output. Output files capped at `ToolConfig.MaxOutputChars` (32K default) with
truncation. Stale job output cleaned up on a retention policy (same approach
as reminder history). The `check_background_job` tool returns a tail of the
output plus the file path for full access.

**[Risk] Session passivated before job handle returned** → The pipeline
awaits `BackgroundJobStarted` (an Ask) before returning the job handle to the
LLM. This is a fast in-process message exchange (sub-millisecond). The session
cannot passivate during a turn, so this is safe.

**[Risk] Delivery to a session that has been permanently abandoned** → If no
user ever re-enters the session, the delivery will rehydrate the session,
process the result (the LLM generates a response to no one), and the session
will idle-timeout and passivate again. Same as an unanswered reminder
delivery — the system degrades gracefully.

**[Trade-off] Shell-only vs tool-generic** → Limiting to shell-only simplifies
the initial implementation (process management, output capture, PID tracking
are all shell-specific). The routing infrastructure in `SessionToolExecutionPipeline`
is tool-generic, so extending to MCP tools later requires only adding an
async invocation path in the job execution actor.

## Documentation Follow-Through

This change intentionally does not edit live runtime documentation or skills
ahead of implementation. The implementation PR for this change must update
operator-facing documentation only once the feature behavior is real and
validated.

Required implementation-time documentation updates:

- `src/Netclaw.Cli/Resources/identity/AGENTS.template.md` must add the
  explicit-only background-shell rule and the approval-before-start rule so
  newly initialized agents are told that background execution requires
  `_background: true` and still starts only after the normal approval gate
  succeeds.
- Operator manual and runbook documentation for background shell execution
  must be updated in the same implementation PR to explain how jobs start,
  how to inspect or cancel them, and how completion is delivered back into
  the session.
