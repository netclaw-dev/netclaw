# tool-call-metadata — Delta Spec

## MODIFIED Requirements

### Requirement: Per-call timeout hint

The `_timeout_seconds` field SHALL allow the LLM to request a per-call timeout
override. The value SHALL be clamped to a configurable ceiling
(`ToolConfig.MaxToolTimeoutSeconds`, default 600). Values below the tool's
default timeout SHALL NOT lower the timeout (the default applies). The pipeline
SHALL use the effective value when creating the per-call
`CancellationTokenSource`. Whenever the effective value differs from the
requested value (ceiling clamp or below-floor request), the pipeline SHALL
append a model-facing notice to the tool result stating the requested value,
the applied value, and — for ceiling clamps — steering the model to
`_background: true` for longer work. Silent clamping or silent ignoring of the
hint SHALL NOT occur.

#### Scenario: Timeout hint applied within ceiling

- **GIVEN** `MaxToolTimeoutSeconds` is 600
- **AND** the LLM requests `_timeout_seconds: 300` on a shell_execute call
- **WHEN** the pipeline creates the cancellation token
- **THEN** the timeout is set to 300 seconds
- **AND** no override notice is appended (requested value was honored)

#### Scenario: Timeout hint exceeds ceiling

- **GIVEN** `MaxToolTimeoutSeconds` is 600
- **AND** the LLM requests `_timeout_seconds: 1200`
- **WHEN** the pipeline creates the cancellation token
- **THEN** the timeout is clamped to 600 seconds
- **AND** the tool result includes a notice stating 1200s was requested, 600s
  was applied, and `_background: true` is available for longer work

#### Scenario: Timeout hint below tool default surfaces a notice

- **GIVEN** `ShellTimeoutSeconds` is 60 (shell tool default)
- **AND** the LLM requests `_timeout_seconds: 10`
- **WHEN** the pipeline creates the cancellation token
- **THEN** the timeout remains at 60 seconds (the tool default)
- **AND** the tool result includes a notice stating 10s was requested and the
  60s tool default was applied

#### Scenario: No timeout hint uses default

- **GIVEN** the LLM does not provide `_timeout_seconds`
- **WHEN** the pipeline creates the cancellation token
- **THEN** the existing default timeout applies (60s for shell, 90s for
  general tool execution)
- **AND** no override notice is appended (no intent was expressed)

## ADDED Requirements

### Requirement: Malformed meta values reject the call

The pipeline SHALL reject a tool call carrying a meta key whose value cannot
be parsed as its declared type (`_timeout_seconds` not a positive integer;
`_background` not a boolean) with a tool-result error before dispatch, naming
the meta key, the supplied value, and the expected type. The tool SHALL NOT execute
with default semantics in place of the expressed intent. Validation state SHALL
be computed pipeline-side; the persisted `ToolCallMeta` type SHALL remain
unchanged.

#### Scenario: Unparseable timeout value rejects instead of silently defaulting

- **GIVEN** a tool call with `"_timeout_seconds": "1200ms"`
- **WHEN** the pipeline extracts meta fields
- **THEN** the call is rejected with an error naming `_timeout_seconds`, the
  value `"1200ms"`, and the expected type positive integer
- **AND** the tool does not execute under the default timeout

#### Scenario: Non-boolean background value rejects

- **GIVEN** a tool call with `"_background": "yes"`
- **WHEN** the pipeline extracts meta fields
- **THEN** the call is rejected with an error naming `_background` and the
  expected type boolean
- **AND** the tool does not execute synchronously in place of the request

#### Scenario: Non-integral JSON number for timeout is handled, not thrown

- **GIVEN** a tool call with `"_timeout_seconds": 12.5`
- **WHEN** the pipeline extracts meta fields
- **THEN** extraction does not throw an uncaught exception
- **AND** the call is rejected as present-but-invalid

#### Scenario: Legacy persisted tool call re-drives deterministically

- **GIVEN** a persisted tool call carrying a malformed meta value is re-driven
  after recovery
- **WHEN** the pipeline extracts meta fields
- **THEN** the same rejection error is produced (deterministic on replay)
