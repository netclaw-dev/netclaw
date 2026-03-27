# netclaw-session Delta: Two-Phase Streaming Timeout

## Requirement Change: Two-phase LLM call timeout

**Replaces**: "LLM call timeout produces LlmCallFailed" (single `TurnLlmTimeout`)

The system SHALL enforce two separate timeout phases for LLM streaming calls:

### Phase 1: First-Token Timeout

The system SHALL wait up to `FirstTokenTimeout` (default 600 seconds) for the
first streaming delta to arrive from the LLM provider. This covers the prefill
phase where the model processes the input context before generating output.

#### Scenario: First-token timeout fires when no deltas arrive

- **GIVEN** an LLM streaming call is in progress
- **AND** no `LlmResponseDeltaReceived` messages have been received
- **WHEN** the `FirstTokenTimeout` elapses
- **THEN** the watchdog fires `ProcessingWatchdogExpired`
- **AND** the turn fails with `ErrorCategory.Timeout`
- **AND** the error message indicates the provider did not respond

### Phase 2: Stream-Idle Timeout

Once the first streaming delta arrives, the system SHALL switch to
`StreamIdleTimeout` (default 120 seconds). This timeout resets on every
subsequent delta. It detects dead streams — connections that are open but
no longer producing tokens.

#### Scenario: Stream-idle timeout fires when stream stalls

- **GIVEN** an LLM streaming call has produced at least one delta
- **WHEN** no further deltas arrive within `StreamIdleTimeout`
- **THEN** the watchdog fires `ProcessingWatchdogExpired`
- **AND** the turn fails with `ErrorCategory.Timeout`
- **AND** the error message indicates the stream stopped

#### Scenario: Active stream resets idle timeout on each delta

- **GIVEN** an LLM streaming call is producing deltas
- **WHEN** a new `LlmResponseDeltaReceived` arrives
- **THEN** the stream-idle timeout resets to `StreamIdleTimeout`

## Requirement Change: No session-level retry on timeout

**Removes**: LLM timeout retry with exponential backoff

The system SHALL NOT retry LLM calls at the session actor level when a timeout
occurs. Timeout failures SHALL immediately fail the current turn.

Rationale: If the provider is alive but slow, the generous first-token timeout
accommodates it. If the provider is dead, retrying wastes time. Transient HTTP
errors are already retried by `RetryingChatClient` at the transport layer.

## Requirement: Backward-compatible timeout configuration

If `TurnLlmTimeoutSeconds` is configured but `FirstTokenTimeoutSeconds` and
`StreamIdleTimeoutSeconds` are not, both phases SHALL use `TurnLlmTimeout` as
their value. This preserves existing single-timeout behavior for operators who
have not updated their configuration.
