# background-job-execution Specification

## Purpose

Define the background job execution infrastructure: manager lifecycle,
process execution, pipeline routing, session state tracking, job monitoring
tool, delivery scoping, and deduplication.

## Requirements

### Requirement: Background job manager lifecycle

The system SHALL provide a `BackgroundJobManagerActor` as an infrastructure-
level singleton registered at daemon startup. The manager SHALL own background
job lifecycle independently of any session. Job definitions SHALL be persisted
to disk at `~/.netclaw/jobs/{id}.json` for reconciliation and diagnostics. The
manager SHALL enforce a configurable concurrency limit (default 5) and queue
overflow in FIFO order. Persistence of job definitions SHALL NOT guarantee
durable execution continuity across daemon restart. When startup
reconciliation marks an orphaned job as lost, the manager SHALL deliver a
termination notification (status `Lost`, output log path) to the originating
session via the standard trusted delivery flow.

#### Scenario: Manager registered at startup

- **WHEN** the Netclaw daemon starts
- **THEN** `BackgroundJobManagerActor` is registered as a singleton actor
- **AND** it performs a best-effort reconciliation pass over persisted job
  definitions

#### Scenario: Concurrency limit enforced

- **GIVEN** 5 background jobs are currently executing
- **WHEN** a new `StartBackgroundJob` command arrives
- **THEN** the job is queued in FIFO order
- **AND** dispatched when a running job completes

#### Scenario: Orphaned process cleanup on restart

- **GIVEN** a job definition exists on disk but the process is no longer
  running (daemon restarted)
- **WHEN** the manager reconciles on startup
- **THEN** the job is marked as lost with reason "process lost during
  restart"
- **AND** a termination notification carrying the `Lost` status and the output
  log path is delivered to the originating session via
  `DeliverTrustedSessionTurn`
- **AND** the streamed output log written before the restart remains available
  at the delivered path

#### Scenario: In-flight job may need relaunch after restart

- **GIVEN** a background job was running when the daemon restarted or went down
- **WHEN** the daemon starts and reconciliation runs
- **THEN** the system makes a best-effort attempt to reconcile the persisted
  job record
- **AND** the in-flight job is not guaranteed to resume or complete
- **AND** the originating session is notified so the user or agent can relaunch
  the job

### Requirement: Background job execution

The system SHALL spawn a `BackgroundJobExecutionActor` child of the manager
for each background job. A background job SHALL be a detached process with no
expectation of completion: the execution actor SHALL NOT require process exit
to make output observable. The execution actor SHALL start the process and
stream stdout/stderr incrementally to `~/.netclaw/jobs/{id}/output.log` as
output is produced, applying secret redaction per line at write time. The
output log SHALL be size-bounded by rotation (current log plus at most one
rotated predecessor) so a long-running chatty process cannot grow disk
unboundedly, and the most recent output SHALL always be available in the
current log. On process exit (whenever it occurs), the actor SHALL deliver the
result to the originating session via `DeliverTrustedSessionTurn`. This
trusted delivery SHALL be an intentional parity decision with normal
synchronous shell tool results, not a trust escalation beyond the original
tool execution. On timeout (only when an explicit timeout was requested), the
actor SHALL kill the entire process tree. The completion result's output tail
SHALL be read back from the on-disk log, not from process-lifetime memory.

#### Scenario: Process started and monitored

- **GIVEN** a `StartBackgroundJob` command is accepted
- **WHEN** the execution actor starts
- **THEN** the process is spawned with stdin closed
- **AND** stdout/stderr stream to the output log file as they are produced
- **AND** the manager returns a `BackgroundJobStarted` response with the job ID

#### Scenario: Output is observable while the process runs

- **GIVEN** a background job is running and has produced output
- **WHEN** the output log file is read (by `check_background_job`, `file_read`,
  or `grep`) before the process exits
- **THEN** the output produced so far is present in the file
- **AND** each line was redacted for secrets before being written

#### Scenario: Log rotation bounds disk usage

- **GIVEN** a running background job whose output exceeds the rotation
  threshold
- **WHEN** the threshold is crossed
- **THEN** the current log rotates to the single rotated-predecessor slot
  (replacing any earlier rotation)
- **AND** streaming continues into a fresh current log
- **AND** total on-disk output for the job remains bounded

#### Scenario: Process completes successfully

- **GIVEN** a background job process exits with code 0
- **WHEN** the execution actor detects the exit
- **THEN** the result (exit code, output tail read from the on-disk log,
  output file path, original rationale) is delivered to the originating
  session via `DeliverTrustedSessionTurn`
- **AND** the job definition is updated to completed status

#### Scenario: Process fails

- **GIVEN** a background job process exits with non-zero code
- **WHEN** the execution actor detects the exit
- **THEN** the failure result (exit code, error output, original rationale) is
  delivered to the originating session via `DeliverTrustedSessionTurn`
- **AND** the job definition is updated to failed status

#### Scenario: Process timeout

- **GIVEN** a background job was submitted with an explicit positive
  `_timeout_seconds` and exceeds it
- **WHEN** the timeout fires
- **THEN** the entire process tree is killed
- **AND** a timeout error is delivered to the originating session
- **AND** the job definition is updated to timed-out status

#### Scenario: Job result delivery to passivated session

- **GIVEN** a background job completes while its termination is in flight
  during or after session passivation
- **WHEN** the result is delivered via `DeliverTrustedSessionTurn`
- **THEN** the session rehydrates from the Akka Persistence journal
- **AND** processes the result turn
- **AND** job-ID deduplication prevents double-processing if the job was also
  marked reaped

#### Scenario: Trusted delivery preserves synchronous shell parity

- **GIVEN** a background job was started from an approved `shell_execute` call
- **WHEN** the job completes
- **THEN** the completion is delivered as a trusted turn
- **AND** that trust matches the trust level of the same shell result if it had
  completed synchronously
- **AND** no broader trust is granted than the persisted originating
  audience/boundary

### Requirement: Pipeline routing to background execution

`SessionToolExecutionPipeline` SHALL evaluate ACL grants and approval policy in
the normal session-owned tool execution path before creating any
`StartBackgroundJob` command or handing a call to `BackgroundJobManagerActor`.
When `ToolCallMeta.Background` is true, the pipeline SHALL route the call to
background execution only after `shell_execute` has passed ACL checks and any
required approval has been granted. Denied, timed-out, or otherwise unapproved
calls SHALL create no background job, SHALL persist no job definition, and
SHALL NOT be handed to `BackgroundJobManagerActor`.
Background routing SHALL only apply to `shell_execute` tool calls. For
non-shell tools, background signals SHALL be logged and ignored (synchronous
execution proceeds).
When no `_timeout_seconds` hint is present, the pipeline SHALL submit the job
with no kill timer — the job runs until it exits, is cancelled, or its owning
session passivates. The pipeline SHALL NOT substitute the synchronous shell
default timeout for background jobs. The job-handle tool result returned to
the LLM SHALL include the output log path so the agent can monitor the job
without an additional query.

#### Scenario: Timeout hint alone remains synchronous

- **GIVEN** the LLM calls `shell_execute` with `_timeout_seconds: 300`
- **AND** `_background` is absent or false
- **AND** the session ACL allows `shell_execute`
- **AND** any required approval has already been granted
- **WHEN** the pipeline processes the tool call
- **THEN** the call executes synchronously with the requested timeout hint
- **AND** no background job is created

#### Scenario: Explicit background flag triggers background

- **GIVEN** the LLM calls `shell_execute` with `_background: true`
- **AND** the session ACL allows `shell_execute`
- **AND** any required approval has already been granted
- **WHEN** the pipeline processes the tool call
- **THEN** the call is routed to `BackgroundJobManagerActor`
- **AND** the tool result returned to the LLM is a job handle that includes the
  output log path

#### Scenario: Omitted timeout hint means no kill timer

- **GIVEN** the LLM calls `shell_execute` with `_background: true` and no
  `_timeout_seconds`
- **WHEN** the pipeline creates `StartBackgroundJob`
- **THEN** the job is submitted with no kill timer
- **AND** the synchronous shell default timeout is not applied to the job

#### Scenario: Approval is required before StartBackgroundJob

- **GIVEN** the LLM calls `shell_execute` with `_background: true`
- **AND** the session ACL allows `shell_execute`
- **AND** `shell_execute` requires approval for the active audience
- **WHEN** the pipeline processes the tool call
- **THEN** the call remains in the session-owned approval path until approval is
  resolved
- **AND** no `StartBackgroundJob` command is created before approval succeeds

#### Scenario: Denied approval creates no background job

- **GIVEN** the LLM calls `shell_execute` with `_background: true`
- **AND** the session ACL allows `shell_execute`
- **AND** `shell_execute` requires approval for the active audience
- **WHEN** the user denies the approval request
- **THEN** the tool returns a denial result
- **AND** no background job is created
- **AND** no job definition is persisted
- **AND** nothing is handed to `BackgroundJobManagerActor`

#### Scenario: Approval timeout creates no background job

- **GIVEN** the LLM calls `shell_execute` with `_background: true`
- **AND** the session ACL allows `shell_execute`
- **AND** `shell_execute` requires approval for the active audience
- **WHEN** the approval request times out
- **THEN** the tool returns an approval-timeout result
- **AND** no background job is created
- **AND** no job definition is persisted
- **AND** nothing is handed to `BackgroundJobManagerActor`

#### Scenario: Only approved calls may be handed to BackgroundJobManagerActor

- **GIVEN** the LLM calls `shell_execute` with `_background: true`
- **AND** the session ACL allows `shell_execute`
- **AND** `shell_execute` requires approval for the active audience
- **WHEN** the user approves the request
- **THEN** the pipeline creates `StartBackgroundJob`
- **AND** the approved call is handed to `BackgroundJobManagerActor`
- **AND** the tool result returned to the LLM is a job handle

#### Scenario: Non-shell tool ignores background signal

- **GIVEN** the LLM calls `web_search` with `_background: true`
- **WHEN** the pipeline processes the tool call
- **THEN** the call executes synchronously
- **AND** a log message notes that background was requested for a non-shell
  tool

### Requirement: Session tracks active background jobs

`SessionState` SHALL maintain an `ActiveBackgroundJobs` dictionary
(job ID → `ActiveJobInfo`) persisted to the Akka journal. `ActiveJobInfo`
SHALL carry `JobId`, `Command`, `Rationale`, and `StartedAt`, and SHALL be
able to record that the job was reaped at passivation. Entries SHALL be added
when a job starts and removed when the result is delivered. Entries marked
reaped SHALL be surfaced to the LLM in the active-jobs context block on the
session's next rehydration (including the output log path) and SHALL be pruned
after the next completed turn, so the agent learns of the reap exactly once.
The working context or system prompt SHALL surface pending jobs to the LLM.

#### Scenario: Job added to session state on start

- **GIVEN** the pipeline routes a tool call to background execution
- **WHEN** `BackgroundJobStarted` is received
- **THEN** an `ActiveJobInfo` entry is persisted to `SessionState`

#### Scenario: Job removed from session state on delivery

- **GIVEN** a background job result is delivered to the session
- **WHEN** the session processes the delivery turn
- **THEN** the corresponding entry is removed from `ActiveBackgroundJobs`

#### Scenario: Active jobs visible after compaction

- **GIVEN** a session with active background jobs has been compacted
- **WHEN** the LLM receives the post-compaction context
- **THEN** the working context includes a list of pending background jobs
  with their rationales

#### Scenario: Active jobs survive session recovery

- **GIVEN** a session with active background jobs has been passivated
- **WHEN** the session rehydrates from the journal
- **THEN** `ActiveBackgroundJobs` is restored from persisted state

#### Scenario: Reaped job surfaced once on rehydration

- **GIVEN** a session passivated with running jobs that were reaped
- **WHEN** the session rehydrates and assembles context for the next turn
- **THEN** the active-jobs context block lists the reaped jobs with reaped
  status and output log path
- **AND** after the next turn completes, the reaped entries are pruned from
  `ActiveBackgroundJobs`

### Requirement: Background jobs are reaped on session passivation

When a session enters passivation, it SHALL request that the background job
manager kill all running or pending jobs owned by that session, and SHALL wait
for the manager's acknowledgement (bounded by a short timeout) before taking
its final snapshot. The manager SHALL kill each owned job's entire process
tree and mark the job definitions with a distinct `Reaped` status,
distinguishable from agent- or user-initiated cancellation. Reaping SHALL NOT
produce a `DeliverTrustedSessionTurn` — delivering a turn would rehydrate the
session being torn down. If the acknowledgement times out, the session SHALL
log the failure loudly and proceed with passivation; the manager's kill
operation SHALL be idempotent.

#### Scenario: Running jobs reaped at passivation

- **GIVEN** a session with running background jobs reaches its idle timeout
- **WHEN** the session enters passivation
- **THEN** the manager kills the process trees of all jobs owned by that
  session
- **AND** the job definitions are marked `Reaped`
- **AND** no termination turn is delivered to the session
- **AND** the session's final snapshot records the jobs as reaped

#### Scenario: Reap handshake completes before final snapshot

- **GIVEN** a session entering passivation with running jobs
- **WHEN** the session requests the reap
- **THEN** the session waits for the manager's acknowledgement before taking
  the final snapshot

#### Scenario: Reap acknowledgement timeout fails loud and proceeds

- **GIVEN** the manager does not acknowledge the reap request within the
  handshake timeout
- **WHEN** the timeout elapses
- **THEN** the session logs the failure explicitly
- **AND** passivation proceeds
- **AND** a subsequent retry or daemon shutdown still terminates the process
  (kill is idempotent; processes do not outlive the daemon)

#### Scenario: Completion racing passivation is not double-processed

- **GIVEN** a job completes at the same time its owning session passivates
- **WHEN** both the completion delivery and the reap occur
- **THEN** job-ID deduplication ensures the session processes at most one
  terminal outcome for the job

### Requirement: check_background_job tool

The system SHALL provide a `check_background_job` tool only when shell
execution is available. The tool SHALL use the `shell` grant category and SHALL
accept a `JobId` parameter and an optional `Cancel` boolean parameter. When
`Cancel` is false or absent, the tool SHALL return job status (running,
completed, failed, cancelled, timed_out), output tail (last N characters if
still running, full truncated result if complete), and the output file path.
Job lookup, status read, and cancellation SHALL be restricted to the
originating session and the persisted originating audience/boundary captured at
job start. When `Cancel` is true, the tool SHALL kill the process tree and mark
the job as cancelled. If the job ID is unknown, or if the caller's
session/audience/boundary does not match the persisted originating values for
that job, the tool SHALL return the same generic `job not found` result.

#### Scenario: Job tool unavailable without shell execution

- **GIVEN** shell execution is not available to the session
- **WHEN** tool definitions are built for the LLM
- **THEN** `check_background_job` is not included in the available tool surface

#### Scenario: Check running job status

- **GIVEN** a background job is running
- **WHEN** the LLM calls `check_background_job` with the job ID
- **THEN** the tool returns status "running", elapsed time, and a tail of
  the current output

#### Scenario: Check completed job result

- **GIVEN** a background job has completed
- **WHEN** the LLM calls `check_background_job` with the job ID
- **THEN** the tool returns status "completed", exit code, truncated output,
  and the output file path

#### Scenario: Cancel running job

- **GIVEN** a background job is running
- **WHEN** the LLM calls `check_background_job` with `Cancel: true`
- **THEN** the process tree is killed
- **AND** the job is marked as cancelled
- **AND** the tool returns confirmation of cancellation

#### Scenario: Cancel non-existent job

- **GIVEN** no job exists with the specified ID
- **WHEN** the LLM calls `check_background_job`
- **THEN** the tool returns an error indicating the job was not found

#### Scenario: Session mismatch is indistinguishable from unknown job

- **GIVEN** a background job exists for a different originating session or a
  different persisted originating audience/boundary
- **WHEN** the LLM calls `check_background_job` with that job ID
- **THEN** the tool returns the same generic `job not found` result used for an
  unknown job ID

### Requirement: Job delivery carries originating audience

Background job results delivered via `DeliverTrustedSessionTurn` SHALL carry
the originating session's `TrustAudience` and trust boundary. The job
definition SHALL persist these values at creation time as `required`,
non-optional fields. Trusted delivery SHALL be scoped to that originating
session and persisted originating audience/boundary only.

Background-job submission SHALL fail loudly when no turn source is present. The
submission path SHALL NOT default a missing audience to `TrustAudience.Personal`
or a missing boundary to the personal boundary; a missing turn source is a
programming error and SHALL raise an explicit exception.

#### Scenario: Job delivery uses originating audience

- **GIVEN** a background job was started from a Personal-audience session
- **WHEN** the job completes and delivers results
- **THEN** `DeliverTrustedSessionTurn` carries `TrustAudience.Personal`
- **AND** the session processes the turn with Personal-level grants

#### Scenario: Trusted delivery remains scoped to originating boundary

- **GIVEN** a background job was started with a specific originating trust
  boundary
- **WHEN** the job completes and delivers results
- **THEN** the delivery uses that persisted originating trust boundary
- **AND** the result is not delivered with a broader boundary than the one
  stored at job creation time

#### Scenario: Submission without a turn source fails loud

- **WHEN** background-job submission is reached without a turn source
- **THEN** the submission throws an explicit exception
- **AND** no job is created with a substituted `Personal` audience or boundary

### Requirement: Persisted job records carry required trust fields

The persisted `BackgroundJobDefinition` and `ActiveJobInfo` records SHALL
declare their audience and boundary fields as `required` and non-optional, so
that every in-process construction is enforced by the compiler. A legacy
`BackgroundJobDefinition` JSON document that lacks these fields SHALL be
rejected at load — the job store SHALL log an error naming the document and the
missing fields and SHALL exclude the document from `Get` and `List`. The system
SHALL NOT substitute an audience or boundary for a job with no persisted trust
context — neither the previous `Personal` default nor a `Public` fallback.

#### Scenario: Legacy job document is rejected at load

- **GIVEN** a persisted `BackgroundJobDefinition` JSON document that predates
  this change and lacks an audience or boundary field
- **WHEN** the job store reads it
- **THEN** the document is excluded — `Get` returns nothing and `List` omits it
- **AND** an error naming the document and the missing fields is logged
- **AND** no audience or boundary is substituted, so the job does not run

#### Scenario: Current job documents round-trip unchanged

- **GIVEN** a `BackgroundJobDefinition` written after this change with explicit
  audience and boundary
- **WHEN** the job store deserializes it
- **THEN** the audience and boundary are read verbatim with no error logged

### Requirement: Job deduplication

The session SHALL maintain dedup state for job deliveries to prevent double-
processing if a delivery is retried. The dedup key SHALL be the job ID. The
pattern SHALL mirror `SessionState.ProcessedReminderIds`.

#### Scenario: Duplicate delivery ignored

- **GIVEN** a job result has already been delivered and processed
- **WHEN** the same delivery is retried (e.g., after a crash recovery)
- **THEN** the session recognizes the job ID as already processed
- **AND** the duplicate delivery is acknowledged without re-processing
