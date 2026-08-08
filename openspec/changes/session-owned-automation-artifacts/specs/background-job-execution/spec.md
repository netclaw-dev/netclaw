## MODIFIED Requirements

### Requirement: Background job manager lifecycle

The system SHALL provide one `BackgroundJobManagerActor` as a daemon infrastructure actor. The manager SHALL coordinate jobs across all sessions.

The source session SHALL own each job definition and output directory. The manager SHALL persist them under:

`~/.netclaw/sessions/{session-key}/jobs/`

The manager SHALL enforce the current global concurrency limit and FIFO queue. File ownership SHALL NOT create one manager or one capacity limit per session.

Job persistence SHALL NOT guarantee process continuity across a daemon restart. Startup reconciliation SHALL mark an orphaned job as lost.

The manager SHALL deliver the lost result and output path to the source session through the standard trusted route.

#### Scenario: Manager registered at startup

- **WHEN** the Netclaw daemon starts
- **THEN** one `BackgroundJobManagerActor` is registered
- **AND** it scans each fixed session job directory
- **AND** it runs the current best-effort reconciliation

#### Scenario: Concurrency limit remains global

- **GIVEN** the global concurrency limit has five active jobs across multiple sessions
- **WHEN** another session submits a job
- **THEN** the manager places that job in the FIFO queue
- **AND** it starts the job after global capacity becomes available

#### Scenario: Orphaned process cleanup on restart

- **GIVEN** a session-owned job definition exists but its process does not run
- **WHEN** the manager reconciles after a daemon restart
- **THEN** the manager marks the job as lost with reason "process lost during restart"
- **AND** it delivers a `Lost` result and the session-owned output path to the source session
- **AND** the output written before the restart remains available

#### Scenario: In-flight job can require a new launch after restart

- **GIVEN** a background job ran before the daemon stopped
- **WHEN** the daemon starts and reconciliation runs
- **THEN** the manager performs the current best-effort reconciliation
- **AND** it does not guarantee that the prior process resumes
- **AND** it notifies the source session when the agent must start a new job

#### Scenario: Manager lookup uses session ownership

- **GIVEN** a job command contains the job ID and source `SessionId`
- **WHEN** the manager reads or updates the job
- **THEN** the store resolves the canonical path under that exact session directory
- **AND** another trusted root does not satisfy the owner check

### Requirement: Background job execution

The system SHALL create one `BackgroundJobExecutionActor` child for each job. The daemon manager SHALL remain the parent actor.

The execution actor SHALL start a detached process and close its standard input. It SHALL not require process exit before it exposes output.

The actor SHALL write redacted standard output and standard error to:

`~/.netclaw/sessions/{session-key}/jobs/{jobId}/output.log`

The actor SHALL keep the current bounded rotation policy. The current log and at most one prior log SHALL remain on disk.

On process exit, the actor SHALL deliver the result to the source session through `DeliverTrustedSessionTurn`.

This route SHALL preserve parity with the approved synchronous shell result. It SHALL not grant more trust than the source tool execution.

An explicit timeout SHALL kill the full process tree. The completion result SHALL read its output tail from the session-owned log.

#### Scenario: Process started and monitored

- **GIVEN** the manager accepts a `StartBackgroundJob` command
- **WHEN** the execution actor starts
- **THEN** the actor starts the process with standard input closed
- **AND** it streams redacted output to the source session job directory
- **AND** the manager returns the job ID and session-owned output path

#### Scenario: Output is observable while the process runs

- **GIVEN** a background job has produced output
- **WHEN** the source session uses `check_background_job`, `file_read`, or `grep`
- **THEN** the current output is present in the session-owned log
- **AND** each line passed through secret redaction before the write

#### Scenario: Log rotation bounds disk usage

- **GIVEN** a background job output exceeds the rotation threshold
- **WHEN** the threshold is crossed
- **THEN** the current log replaces the single prior-log slot
- **AND** output continues in a new current log
- **AND** the job output remains size-bounded

#### Scenario: Process completes successfully

- **GIVEN** a background job process exits with code 0
- **WHEN** the execution actor detects the exit
- **THEN** it reads the output tail from the session-owned log
- **AND** it delivers the exit code, output path, output tail, and rationale to the source session
- **AND** it marks the job definition as completed

#### Scenario: Process fails

- **GIVEN** a background job process exits with a nonzero code
- **WHEN** the execution actor detects the exit
- **THEN** it delivers the failure, output, output path, and rationale to the source session
- **AND** it marks the job definition as failed

#### Scenario: Process reaches its timeout

- **GIVEN** a background job has an explicit positive `_timeout_seconds`
- **AND** the process exceeds that value
- **WHEN** the timeout fires
- **THEN** the actor kills the full process tree
- **AND** it delivers the timeout error to the source session
- **AND** it marks the job definition as timed out

#### Scenario: Job result reaches a passivated session

- **GIVEN** a background job completes during or after source session passivation
- **WHEN** the result arrives through `DeliverTrustedSessionTurn`
- **THEN** the source session recovers from its Akka Persistence journal
- **AND** it processes the result turn
- **AND** job-ID deduplication prevents a duplicate result after a reap race

#### Scenario: Trusted delivery preserves synchronous shell parity

- **GIVEN** an approved `shell_execute` call started a background job
- **WHEN** the job completes
- **THEN** the completion uses a trusted turn
- **AND** that trust equals the approved synchronous shell result trust
- **AND** the stored audience and boundary do not become broader

## ADDED Requirements

### Requirement: Legacy background job artifact migration

On startup, the job store SHALL inspect legacy definitions under `~/.netclaw/jobs/`. It SHALL derive each destination from the stored `SessionId`.

The store SHALL move the job output directory before it moves the definition file. The definition move SHALL act as the migration commit point.

The store SHALL NOT overwrite a destination artifact. A conflict or invalid owner SHALL produce an error and preserve the source data.

The store SHALL continue to resolve a valid legacy job after a migration error. This compatibility path SHALL produce an operator-visible log entry.

#### Scenario: Legacy job moves to its source session

- **GIVEN** a valid legacy job definition contains a valid `SessionId`
- **AND** no destination artifact exists
- **WHEN** startup migration runs
- **THEN** the output directory moves to the canonical session job directory when present
- **AND** the definition moves to the same session job directory
- **AND** the job keeps its current ID and status

#### Scenario: Migration conflict preserves legacy job

- **GIVEN** a legacy job and its destination path both exist
- **WHEN** startup migration runs
- **THEN** the migration does not overwrite either artifact
- **AND** the store logs the conflict
- **AND** the manager can still inspect the source job through the explicit compatibility path

#### Scenario: Restart resumes an interrupted job migration

- **GIVEN** a prior migration moved the output directory but did not move the definition
- **WHEN** the daemon restarts
- **THEN** the migration accepts the matching destination output directory
- **AND** it completes the definition move without output loss

#### Scenario: Invalid session owner fails loud

- **GIVEN** a legacy job definition has an invalid or absent `SessionId`
- **WHEN** startup migration runs
- **THEN** the store does not move the job
- **AND** it logs the path and validation error
- **AND** the manager does not execute the job under a substituted session
