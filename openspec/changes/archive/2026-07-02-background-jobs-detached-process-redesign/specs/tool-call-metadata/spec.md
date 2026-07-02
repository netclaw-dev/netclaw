# tool-call-metadata Delta

## MODIFIED Requirements

### Requirement: Per-call timeout hint

The `_timeout_seconds` field SHALL allow the LLM to set a per-call timeout. A
positive value SHALL be honored exactly — it SHALL NOT be clamped to a ceiling
nor floored to the tool default; the agent owns this judgement. When no hint is
provided, the inherited per-call default (`SessionConfig.ToolExecutionTimeout`)
SHALL apply to synchronous execution only. When no hint is provided and the
call is routed to background execution (`_background: true`), no kill timer
SHALL be applied — a background job is a detached process with no completion
expectation, terminated by its own exit, cancellation, or its owning session's
passivation. The pipeline SHALL use the synchronous value when creating the
per-call `CancellationTokenSource`, and an explicit positive hint SHALL govern
the background-job path when `_background` is set. (A present-but-invalid
value — non-positive or unparseable — is rejected before dispatch; see
"Malformed meta values".)

#### Scenario: Timeout hint is honored exactly

- **GIVEN** the LLM requests `_timeout_seconds: 1200` on a shell_execute call
- **WHEN** the pipeline creates the cancellation token
- **THEN** the timeout is set to 1200 seconds
- **AND** nothing is appended to the tool result

#### Scenario: A small timeout hint is honored, not floored

- **AND** the LLM requests `_timeout_seconds: 10`
- **WHEN** the pipeline creates the cancellation token
- **THEN** the timeout is set to 10 seconds (no floor is imposed)

#### Scenario: No timeout hint uses the inherited default

- **GIVEN** the LLM does not provide `_timeout_seconds`
- **WHEN** the pipeline creates the cancellation token for synchronous
  execution
- **THEN** the `SessionConfig.ToolExecutionTimeout` default applies

#### Scenario: Background path honors the same hint

- **GIVEN** the LLM sets `_background: true` and `_timeout_seconds: 1800`
- **WHEN** the call is routed to a background job
- **THEN** the job's timeout is 1800 seconds (not clamped)

#### Scenario: Background path without a hint gets no kill timer

- **GIVEN** the LLM sets `_background: true` and omits `_timeout_seconds`
- **WHEN** the call is routed to a background job
- **THEN** the job is submitted with no kill timer
- **AND** the synchronous default timeout is not substituted
