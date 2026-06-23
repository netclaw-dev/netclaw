# Background Shell Execution

A background job is a **detached process with no expectation of completion**.
It runs shell commands outside the session actor's processing loop — long
builds, test suites, dev servers, watchers. Its output streams to an on-disk
log while it runs, and its termination — by its own exit, an explicit timeout,
a cancel, session passivation, or a daemon restart — is reported to the owning
session as a notification.

Background jobs are **session-scoped**: when the owning session passivates
(conversation idle past the idle timeout), its jobs are killed (`Reaped`). They
do not survive daemon restarts either (`Lost`). For work that must run
unattended past the conversation, use a scheduled task.

## How it works

### Submission

The agent sets `_background: true` in the `shell_execute` tool call metadata.
The pipeline:

1. Evaluates approval gates (user must approve before the job starts)
2. Sends `StartBackgroundJob` to the `BackgroundJobManagerActor` singleton
3. Returns a synthetic tool result with the job ID **and output log path**
4. Persists `ActiveJobInfo` to session state

Only `shell_execute` supports background mode. Other tools ignore `_background`
and execute synchronously with a warning log.

### Execution

The `BackgroundJobManagerActor` manages job lifecycle:

- **Concurrency limit**: 5 concurrent jobs. Overflow goes to a FIFO queue.
- **Process isolation**: each job runs as a child `BackgroundJobExecutionActor`
  that spawns the shell process with stdin closed.
- **Streaming output capture**: stdout/stderr stream line-by-line to
  `~/.netclaw/jobs/{id}/output.log` *while the process runs* (stderr lines
  prefixed `[stderr]`). Each line is secret-redacted at write time. The log is
  bounded by single-slot rotation: when `output.log` crosses ~5 MB it moves to
  `output.1.log` (replacing any earlier rotation), so a job holds at most
  ~10 MB on disk and the most recent output is always in `output.log`.
- **Timeout**: a kill timer is armed **only** when the agent passes a positive
  `_timeout_seconds`. Omitted means no timer — the job runs until it exits or
  is reaped.
- **Definitions**: persisted to `~/.netclaw/jobs/{id}.json` for crash recovery.

### Termination

Every terminal transition is reported:

| Cause | Status | Session notification |
|-------|--------|----------------------|
| Process exits (any code) | `Completed` / `Failed` | Result turn via `DeliverTrustedSessionTurn` (rehydrates a passivated session) |
| Explicit `_timeout_seconds` exceeded | `TimedOut` | Result turn |
| `check_background_job(Cancel: true)` | `Cancelled` | Result turn |
| Owning session passivates | `Reaped` | **No turn** (a turn would rehydrate the session being torn down) — surfaced once in `[active-background-jobs]` on the next rehydration, then pruned |
| Daemon restart | `Lost` | Result turn at reconciliation, with the log path (the streamed log survives the crash) |

Result-turn flow: the manager updates the persisted definition, constructs a
`DeliverTrustedSessionTurn`, routes it to the originating channel's gateway
(Slack, SignalR, TUI), the gateway routes it to the session actor (rehydrating
if passivated), and the session processes it as a trusted turn with job-ID
dedup protection.

### Reap on passivation

When a session enters passivation it sends `KillJobsForSession` to the manager
and waits for the acknowledgement (5s handshake timeout) before its final
snapshot, so the snapshot captures the reaped marks. The manager kills each
owned job's process tree, marks the definitions `Reaped`, and suppresses the
children's completion deliveries. If the ack times out, the session logs the
failure loudly and passivates anyway — the manager's kill is idempotent and no
job process outlives the daemon.

### Startup reconciliation

On daemon restart, the manager scans `~/.netclaw/jobs/` for definitions with
status `Running` or `Pending`. These are orphaned processes lost during the
restart — marked `Lost` with a completion timestamp, **and the owning session
is notified** with the log path so the agent can relaunch. Notification volume
is bounded by design: passivated sessions have no live jobs, so only sessions
that were warm at crash time appear here.

## Monitoring

### Active jobs in context

Active background jobs are surfaced in the session's system prompt under
`[active-background-jobs]`: job ID, command, rationale, output log path, and —
after a passivation kill — `status: reaped`. Reaped entries appear exactly once
and are pruned after the next completed turn.

### Live log

The output log exists from the moment the job starts and fills as the process
writes. `file_read`/`grep` it for readiness signals ("Server running on…"),
progress, or errors — this is the intended way for the agent to wait on a dev
server before driving it with Playwright or curl.

### check_background_job tool

The `check_background_job` tool queries job status or cancels a running job:

```
check_background_job(JobId: "abc123")          # query status
check_background_job(JobId: "abc123", Cancel: true)  # cancel
```

Returns: status, elapsed time, rationale, exit code (if finished), and the
live output tail (last 2000 chars, read from the streaming log). Only
accessible from the same session/audience/boundary that submitted the job.

This tool is only available when shell execution is granted (same `shell` grant
category as `shell_execute`).

## Filesystem layout

```
~/.netclaw/jobs/
├── abc123.json           # job definition (status, command, session, timing)
└── abc123/
    ├── output.log        # live streamed stdout + stderr (most recent)
    └── output.1.log      # rotated predecessor (present only after rotation)
```

## Configuration

The `_timeout_seconds` metadata field on the tool call arms a per-job kill
timer and is honored as requested. When omitted, **no kill timer applies** —
the job is bounded by its own exit, cancellation, or session passivation, not
by a wall-clock default. (Synchronous `shell_execute` calls keep the
`SessionConfig.ToolExecutionTimeout` default; the no-timer rule is specific to
background routing.)

No separate configuration surface exists — background jobs use the same
approval policy and audience ACL as regular shell execution.

## Diagnostics

| Symptom | Check |
|---------|-------|
| Job stuck as "running" | Check `~/.netclaw/jobs/{id}.json` status; daemon may have restarted (jobs become Lost) |
| No result delivered | Check daemon logs for gateway resolution failure; verify channel type matches a registered gateway |
| Job definition shows Lost | Normal after daemon restart — the owning session was notified; the pre-crash log remains readable |
| Job definition shows Reaped | Normal after the owning session went idle — the agent resubmits if still needed |
| Output log empty | The file is created at job start; if it stays empty the process produced no output — check exit code and status |
| Output log smaller than expected | Rotation: earlier output is in `output.1.log`; only ~10 MB total is retained per job |
| Concurrency queue growing | 5 jobs already running (long-lived servers hold slots until cancelled or reaped); jobs execute FIFO when slots free up |
| Reap handshake timeout in logs | Manager was unresponsive at passivation; kills are idempotent and re-applied at daemon teardown |
