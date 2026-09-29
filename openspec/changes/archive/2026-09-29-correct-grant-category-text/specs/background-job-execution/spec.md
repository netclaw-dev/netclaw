## MODIFIED Requirements

### Requirement: check_background_job tool

The system SHALL provide a `check_background_job` tool only when shell
execution is available. Its admission SHALL follow `shell_execute`: the
Personal audience and a host shell mode (`tool-authorization` TA-4). Its
`shell` grant category is metadata only. The tool SHALL accept a `JobId` parameter and an optional `Cancel` boolean parameter. When
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
