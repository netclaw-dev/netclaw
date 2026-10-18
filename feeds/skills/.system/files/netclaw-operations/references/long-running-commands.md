# Long-Running Commands

Read this page when a command times out, or when Netclaw refuses a repeated call.

## Signs

- A `shell_execute` result says that the command timed out. The default foreground limit is 90 seconds.
- A tool result says that Netclaw stopped the call because it would continue a repeated action-and-outcome cycle.
- A system nudge says that Netclaw refused your repeated call again.

A refused call did not execute. Do not report its work as done.

## How the refusal works

1. Two rounds of the same tool, the same arguments, and the same result make a cycle.
2. Netclaw refuses the next identical call. That call does not execute.
3. Netclaw refuses each later identical call again and adds a nudge. The turn continues.
4. A new message from the person starts a new cycle window. Context compaction does not.

The call identity uses the tool name and its arguments.

- `_rationale` and `_timeout_seconds` are not part of the identity. A new rationale or a new timeout alone is the same call.
- A valid `_background: true` is part of the identity. A background run of a refused command is a different call.
- An authorized status query for a pending background job can repeat.

Netclaw adds no count limit for refusals. Each repeat costs one model request and gives no new result.

## Select a different action

Use the first option that fits the work.

1. **Set a longer timeout before a refusal.** Set `_timeout_seconds` on the next call when the command needs more time than the default. Netclaw uses the value that you set. After a refusal, a new timeout alone does not change the call.
2. **Run the command as a background job.** Set `_background: true` on `shell_execute`. See [Background jobs](#background-jobs).
3. **Divide the work.** Run smaller steps that each finish within the limit. For example, process one file at a time.
4. **Change the command or its arguments.** Use a faster mode, a smaller input, or a command that reports progress to a file.
5. **Check the machine.** A slow command can show that the host is busy. Look at load and free memory before you try again.
6. **Ask the person.** Tell the person what timed out and ask how to continue.

Do not:

- Send the identical command again.
- Run a shell loop that sleeps and polls until a command finishes. Use a background job and `check_background_job`.

A child run has no background job manager. In a child run, stop and give your result to the parent. Name the command that did not finish, so that the parent can run it as a background job.

## Background jobs

A background job is a detached process with no completion expectation. Use it for work that lasts longer than one tool call: long builds, test suites, renders, dev servers, and watchers.

Submit:

- Set `_background: true` in the `shell_execute` call metadata.
- Approval gates apply before the job starts.
- The submit result includes the job ID and the output log path.

Monitor:

- `check_background_job(JobId: "id")` returns the status, the elapsed time, and a live output tail.
- The full output streams to `~/.netclaw/jobs/{id}/output.log`. Netclaw redacts secrets and limits its size. Read it with `file_read` or `grep`.
- Probe a service directly, for example with `curl` to its port, after the log shows that it started.
- `check_background_job(JobId: "id", Cancel: true)` cancels a job. Cancel servers and watchers when you finish with them.

Lifecycle:

- Without `_timeout_seconds`, a background job has no kill timer. A positive `_timeout_seconds` sets a kill timer.
- When the process exits, Netclaw starts a result turn with the exit code, the output tail, and the log path.
- Netclaw stops jobs when the session passivates after the idle timeout. The job then shows as `reaped` in `[active-background-jobs]`. Its output log stays readable.
- Netclaw stops running jobs on a daemon restart and sends a `lost` notification with the log path.
- Netclaw keeps a terminal job definition and its logs for 24 hours. Then the cleanup sweep deletes them.

Rules:

- Only `shell_execute` supports background mode. Other tools ignore `_background`.
- `_timeout_seconds` alone does not start a background job.
- A synchronous `_timeout_seconds` has no ceiling and no floor. Without it, the default tool timeout applies.
- A synchronous timeout is a wall-clock budget. Repeated output does not extend it.
- The parent applies no timeout to `spawn_agent`. The child owns its liveness and always returns a final result.
- Run a long delegation call as a background job, for example `curl` to a local coding agent or model server.
- The person must approve the command before it starts in the background.
- Netclaw runs at most 5 background jobs at a time. More jobs wait in a queue.
- A queued job gets a policy check again when its slot opens. A revoked grant or a newly protected path can cause a failed launch.
- A queued job keeps its exact command, working directory, shell, and environment.
- Use an absolute `WorkingDirectory`, or omit it to use the session or project directory.
- Job definitions stay in `~/.netclaw/jobs/{id}.json` until 24 hours after the job ends.
- `check_background_job` is available only when `shell_execute` is available: the Personal audience with a host shell mode. It checks that the session has the audience and boundary of the session that submitted the job.
- Active jobs appear in the `[active-background-jobs]` section of the session context on every turn.

After you submit a long finite job, schedule a check-back reminder. Then you can report the result when the job ends. For an idle conversation, use a scheduled task: a passivated session stops its jobs.
