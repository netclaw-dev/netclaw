# Background Shell Execution

Background jobs run shell commands outside the session actor lifecycle. They
outlive session idle timeouts and passivation — results are delivered
asynchronously via `DeliverTrustedSessionTurn` when the job completes, even
if the session has been passivated and needs rehydration.

## How it works

### Submission

The agent sets `_background: true` in the `shell_execute` tool call metadata.
The pipeline:

1. Evaluates approval gates (user must approve before the job starts)
2. Sends `StartBackgroundJob` to the `BackgroundJobManagerActor` singleton
3. Returns a synthetic tool result with the job ID to the LLM
4. Persists `ActiveJobInfo` to session state

Only `shell_execute` supports background mode. Other tools ignore `_background`
and execute synchronously with a warning log.

### Execution

The `BackgroundJobManagerActor` manages job lifecycle:

- **Concurrency limit**: 5 concurrent jobs. Overflow goes to a FIFO queue.
- **Process isolation**: each job runs as a child `BackgroundJobExecutionActor`
  that spawns the shell process with stdin closed.
- **Output capture**: stdout/stderr written to `~/.netclaw/jobs/{id}/output.log`.
- **Timeout**: process tree killed if `_timeout_seconds` is exceeded.
- **Definitions**: persisted to `~/.netclaw/jobs/{id}.json` for crash recovery.

### Completion

When a job finishes (success, failure, timeout, or cancellation):

1. The execution actor reports `BackgroundJobCompleted` to its parent manager
2. The manager updates the persisted definition
3. The manager constructs a `DeliverTrustedSessionTurn` message with the result
4. The message is routed to the originating channel's gateway (Slack, SignalR, TUI)
5. The gateway routes it to the session actor, rehydrating if passivated
6. The session processes the result as a trusted turn with dedup protection

### Startup reconciliation

On daemon restart, the manager scans `~/.netclaw/jobs/` for definitions with
status `Running` or `Pending`. These are orphaned processes that were lost
during the restart — marked as `Lost` with a completion timestamp.

## Monitoring

### Active jobs in context

Active background jobs are surfaced in the session's system prompt under
`[active-background-jobs]`. The agent sees job IDs, commands, rationale, and
elapsed time on every turn.

### check_background_job tool

The `check_background_job` tool queries job status or cancels a running job:

```
check_background_job(JobId: "abc123")          # query status
check_background_job(JobId: "abc123", Cancel: true)  # cancel
```

Returns: status, elapsed time, rationale, exit code (if finished), and output
tail (last 2000 chars). Only accessible from the same session/audience/boundary
that submitted the job.

This tool is only available when shell execution is granted (same `shell` grant
category as `shell_execute`).

## Filesystem layout

```
~/.netclaw/jobs/
├── abc123.json           # job definition (status, command, session, timing)
└── abc123/
    └── output.log        # captured stdout + stderr
```

## Configuration

Background jobs inherit the session's tool timeout ceiling
(`MaxToolTimeoutSeconds`, default 600s). The `_timeout_seconds` metadata field
on the tool call controls per-job timeout within that ceiling.

No separate configuration surface exists — background jobs use the same
approval policy and audience ACL as regular shell execution.

## Diagnostics

| Symptom | Check |
|---------|-------|
| Job stuck as "running" | Check `~/.netclaw/jobs/{id}.json` status; daemon may have restarted (jobs become Lost) |
| No result delivered | Check daemon logs for gateway resolution failure; verify channel type matches a registered gateway |
| Job definition shows Lost | Normal after daemon restart — orphaned processes can't be recovered |
| Output log empty | Process may have been killed before producing output; check exit code and status |
| Concurrency queue growing | 5 jobs already running; jobs execute FIFO when slots free up |
