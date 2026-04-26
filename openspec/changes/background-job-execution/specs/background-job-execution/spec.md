## ADDED Requirements

### Requirement: Background job manager lifecycle

The system SHALL provide a `BackgroundJobManagerActor` as an infrastructure-
level singleton registered at daemon startup. The manager SHALL own background
job lifecycle independently of any session. Job definitions SHALL be persisted
to disk at `~/.netclaw/jobs/{id}.json`. The manager SHALL enforce a
configurable concurrency limit (default 5) and queue overflow in FIFO order.

#### Scenario: Manager registered at startup

- **WHEN** the Netclaw daemon starts
- **THEN** `BackgroundJobManagerActor` is registered as a singleton actor
- **AND** it reconciles persisted job definitions against running processes

#### Scenario: Concurrency limit enforced

- **GIVEN** 5 background jobs are currently executing
- **WHEN** a new `StartBackgroundJob` command arrives
- **THEN** the job is queued in FIFO order
- **AND** dispatched when a running job completes

#### Scenario: Orphaned process cleanup on restart

- **GIVEN** a job definition exists on disk but the process is no longer
  running (daemon restarted)
- **WHEN** the manager reconciles on startup
- **THEN** the job is marked as failed with reason "process lost during
  restart"
- **AND** the result is delivered to the originating session

### Requirement: Background job execution

The system SHALL spawn a `BackgroundJobExecutionActor` child of the manager
for each background job. The execution actor SHALL start the process, capture
stdout/stderr to `~/.netclaw/jobs/{id}/output.log`, and monitor for process
exit. On process exit, the actor SHALL deliver the result to the originating
session via `DeliverTrustedSessionTurn`. On timeout, the actor SHALL kill the
entire process tree.

#### Scenario: Process started and monitored

- **GIVEN** a `StartBackgroundJob` command is accepted
- **WHEN** the execution actor starts
- **THEN** the process is spawned with stdin closed
- **AND** stdout/stderr are captured to the output log file
- **AND** the manager returns a `BackgroundJobStarted` response with the job ID

#### Scenario: Process completes successfully

- **GIVEN** a background job process exits with code 0
- **WHEN** the execution actor detects the exit
- **THEN** the result (exit code, truncated output, output file path, original
  rationale) is delivered to the originating session via
  `DeliverTrustedSessionTurn`
- **AND** the job definition is updated to completed status

#### Scenario: Process fails

- **GIVEN** a background job process exits with non-zero code
- **WHEN** the execution actor detects the exit
- **THEN** the failure result (exit code, error output, original rationale) is
  delivered to the originating session via `DeliverTrustedSessionTurn`
- **AND** the job definition is updated to failed status

#### Scenario: Process timeout

- **GIVEN** a background job exceeds its timeout
- **WHEN** the timeout fires
- **THEN** the entire process tree is killed
- **AND** a timeout error is delivered to the originating session
- **AND** the job definition is updated to timed-out status

#### Scenario: Job result delivery to passivated session

- **GIVEN** a background job completes
- **AND** the originating session has been passivated (idle timeout)
- **WHEN** the result is delivered via `DeliverTrustedSessionTurn`
- **THEN** the session rehydrates from the Akka Persistence journal
- **AND** processes the result turn

### Requirement: Pipeline routing to background execution

`SessionToolExecutionPipeline` SHALL route tool calls to background execution
when `ToolCallMeta.Background` is true or `ToolCallMeta.TimeoutHintSeconds`
exceeds `ToolConfig.BackgroundThresholdSeconds`. Background routing SHALL only
apply to `shell_execute` tool calls. For non-shell tools, background signals
SHALL be logged and ignored (synchronous execution proceeds).

#### Scenario: Timeout threshold triggers background

- **GIVEN** `BackgroundThresholdSeconds` is 180
- **AND** the LLM calls `shell_execute` with `_timeout_seconds: 300`
- **WHEN** the pipeline processes the tool call
- **THEN** the call is routed to `BackgroundJobManagerActor`
- **AND** the tool result returned to the LLM is a job handle

#### Scenario: Explicit background flag triggers background

- **GIVEN** the LLM calls `shell_execute` with `_background: true`
- **WHEN** the pipeline processes the tool call
- **THEN** the call is routed to `BackgroundJobManagerActor`
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
SHALL carry `JobId`, `Command`, `Rationale`, and `StartedAt`. Entries SHALL
be added when a job starts and removed when the result is delivered. The
working context or system prompt SHALL surface pending jobs to the LLM.

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

### Requirement: check_background_job tool

The system SHALL provide a `check_background_job` tool in the "shell" grant
category. The tool SHALL accept a `JobId` parameter and an optional
`Cancel` boolean parameter. When `Cancel` is false or absent, the tool SHALL
return job status (running, completed, failed, cancelled, timed_out), output
tail (last N characters if still running, full truncated result if complete),
and the output file path. When `Cancel` is true, the tool SHALL kill the
process tree and mark the job as cancelled.

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

### Requirement: Job delivery carries originating audience

Background job results delivered via `DeliverTrustedSessionTurn` SHALL carry
the originating session's `TrustAudience` and trust boundary. The job
definition SHALL persist these values at creation time.

#### Scenario: Job delivery uses originating audience

- **GIVEN** a background job was started from a Personal-audience session
- **WHEN** the job completes and delivers results
- **THEN** `DeliverTrustedSessionTurn` carries `TrustAudience.Personal`
- **AND** the session processes the turn with Personal-level grants

### Requirement: Job deduplication

The session SHALL maintain dedup state for job deliveries to prevent double-
processing if a delivery is retried. The dedup key SHALL be the job ID. The
pattern SHALL mirror `SessionState.ProcessedReminderIds`.

#### Scenario: Duplicate delivery ignored

- **GIVEN** a job result has already been delivered and processed
- **WHEN** the same delivery is retried (e.g., after a crash recovery)
- **THEN** the session recognizes the job ID as already processed
- **AND** the duplicate delivery is acknowledged without re-processing
