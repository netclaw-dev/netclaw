## MODIFIED Requirements

### Requirement: Background job execution

The system SHALL spawn a `BackgroundJobExecutionActor` child of the manager
for each background job. The execution actor SHALL start the process, capture
stdout/stderr to `~/.netclaw/jobs/{id}/output.log`, and monitor for process
exit. Output capture SHALL bound peak managed memory per the `bounded-tool-output`
capability — the execution actor SHALL stream output to the log and retain only a
bounded tail in memory for the completion message, and SHALL NOT materialize the
full output as a single in-memory string before trimming. On process exit, the
actor SHALL deliver the result to the originating session via
`DeliverTrustedSessionTurn`. This trusted delivery SHALL be an intentional parity
decision with normal synchronous shell tool results, not a trust escalation
beyond the original tool execution. On timeout, the actor SHALL kill the entire
process tree.

#### Scenario: Process started and monitored

- **GIVEN** a `StartBackgroundJob` command is accepted
- **WHEN** the execution actor starts
- **THEN** the process is spawned with stdin closed
- **AND** stdout/stderr are captured to the output log file
- **AND** the manager returns a `BackgroundJobStarted` response with the job ID

#### Scenario: Output capture bounds memory

- **GIVEN** a background job process emits output far larger than the capture
  ceiling
- **WHEN** the execution actor captures it
- **THEN** peak managed memory stays on the order of the capture ceiling
- **AND** the full output is not held as a single in-memory string
- **AND** the daemon is not OOM-killed by the capture

#### Scenario: Process completes successfully

- **GIVEN** a background job process exits with code 0
- **WHEN** the execution actor detects the exit
- **THEN** the result (exit code, bounded output tail, output file path, original
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

#### Scenario: Trusted delivery preserves synchronous shell parity

- **GIVEN** a background job was started from an approved `shell_execute` call
- **WHEN** the job completes
- **THEN** the completion is delivered as a trusted turn
- **AND** that trust matches the trust level of the same shell result if it had
  completed synchronously
- **AND** no broader trust is granted than the persisted originating
  audience/boundary
