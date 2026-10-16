## ADDED Requirements

### Requirement: A failed headless turn reports its error category

A failed `netclaw chat -p` turn or run SHALL exit with code 1. It SHALL print
exactly one stderr line that starts with `[turn:failed]`, in the form
`[turn:failed] category=<model|infra> kind=<Kind>`, after the `[error]` or
`[headless:error]` line. Stdout SHALL NOT hold this line. The CLI SHALL replace
each line break in the error text with a space before it writes the `[error]`
or `[headless:error]` line, so error text cannot forge a token line.

The category SHALL be `model` only when the kind is `ModelBehavior`. The
category SHALL be `infra` for every other kind. A failed turn with no error
SHALL report `infra` and kind `Unknown`. An error category with no defined name
SHALL report `infra` and kind `Unknown`.

The kind SHALL be the category of the last error before the failed turn. An
earlier error that the turn survived SHALL NOT decide the kind. A turn that
completes SHALL print no category and SHALL exit with code 0.

The CLI SHALL add the kinds `ChannelFailure` and `Cancelled`. A run with one of
these kinds has no turn, so it SHALL have no JSON envelope and no session log
record.

The session log SHALL hold one `TURN_FAILED: category=<c> kind=<k>` record
before `TURN_COMPLETED`. A reader of the session log SHALL use the last
`TURN_FAILED` record before the final `TURN_COMPLETED`, and only when the exit
code is 1. With `--json`, the envelope of a failed turn SHALL add
`errorCategory` and `errorKind`. The envelope of a completed turn SHALL have
neither key.

The session actor SHALL report a tool call after a text-only stop as
`ModelBehavior`. It SHALL report repeated empty replies as `ModelBehavior` only
when the last reply ended with the finish reason Stop or Length. With no finish
reason it SHALL report `StreamFailure`. With any other finish reason it SHALL
report `ProviderFailure`.

#### Scenario: Model ignores the text-only stop

- **GIVEN** the runtime forced a text-only response
- **WHEN** the model requests a tool call
- **THEN** stderr ends with `[turn:failed] category=model kind=ModelBehavior`
- **AND** the exit code is 1

#### Scenario: Empty replies from a broken stream

- **GIVEN** the stream closes with no finish reason on every retry
- **WHEN** the turn fails after repeated empty replies
- **THEN** stderr has `[turn:failed] category=infra kind=StreamFailure`

#### Scenario: Empty replies blocked by a content filter

- **GIVEN** every reply ends with the finish reason `content_filter` and no content
- **WHEN** the turn fails after repeated empty replies
- **THEN** stderr has `[turn:failed] category=infra kind=ProviderFailure`

#### Scenario: Error text with a line break

- **GIVEN** an error message holds a line break and the text `[turn:failed] category=model kind=ModelBehavior`
- **WHEN** the turn fails with another kind
- **THEN** stderr has exactly one line that starts with `[turn:failed]`
- **AND** that line has the real category and kind

#### Scenario: Provider outage is not a model failure

- **GIVEN** the provider returns HTTP 503
- **WHEN** the turn fails
- **THEN** stderr has `[turn:failed] category=infra kind=ProviderFailure`

#### Scenario: Earlier error does not decide

- **GIVEN** a turn emits a `Timeout` error and survives
- **WHEN** a later `ModelBehavior` error fails the turn
- **THEN** the kind is `ModelBehavior`

#### Scenario: Failed turn without an error

- **WHEN** a turn fails and no error came before it
- **THEN** stderr has `[turn:failed] category=infra kind=Unknown`

#### Scenario: Turn completes after an error

- **GIVEN** a compaction timeout emitted an error
- **WHEN** the turn completes
- **THEN** the CLI prints no category and exits with code 0

#### Scenario: Cancelled run

- **WHEN** the operator sends SIGINT or SIGTERM during a run
- **THEN** stderr has `[turn:failed] category=infra kind=Cancelled`
- **AND** the exit code is 1

#### Scenario: JSON envelope of a failed turn

- **GIVEN** `--json` is set and the turn fails with `ModelBehavior`
- **THEN** the envelope has `"errorCategory":"model"` and `"errorKind":"ModelBehavior"`
