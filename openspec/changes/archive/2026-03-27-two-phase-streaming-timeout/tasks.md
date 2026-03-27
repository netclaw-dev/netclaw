# Tasks: Two-Phase Streaming Timeout

## Phase A: Revert retry system

- [x] Delete `src/Netclaw.Actors/Sessions/Handlers/TimeoutRetryHandler.cs`
- [x] Delete `src/Netclaw.Actors.Tests/Sessions/Handlers/TimeoutRetryHandlerTests.cs`
- [x] Delete `src/Netclaw.Actors.Tests/Sessions/LlmSessionTimeoutRetryTests.cs`
- [x] Remove `RetryLlmCallAfterBackoff` record from `LlmMessages.cs`
- [x] Remove from `LlmSessionActor.cs`: `_timeoutRetry` field, `TimeoutRetryTimerKey`,
  constructor init, `HandleTimeoutRetryOrFail()`, `RetryLlmCallAfterBackoff` handlers
  in `Processing()` and `Ready()`, reset/cancel calls in `HandleIncomingUserMessage`
  and `FailCurrentTurn`
- [x] Restore `LlmCallFailed` handler to direct `FailCurrentTurn` with category check
- [x] Restore `ProcessingWatchdogExpired` handler to direct `FailCurrentTurn`
- [x] Remove `LlmTimeoutMaxRetries` and `LlmTimeoutRetryBaseDelaySeconds` from
  `SessionConfig`, `RawSessionConfig`, `BindFromConfiguration`
- [x] Remove those properties from `netclaw-config.v1.schema.json`
- [x] Remove `LlmTimeoutMaxRetries = 0` from `LlmSessionWatchdogTests.cs` and
  `ErrorCorrelationTests.cs`
- [x] Verify build + existing tests pass

## Phase B: Add two-phase config

- [x] Add `FirstTokenTimeout` (TimeSpan, default 600s) to `SessionConfig`
- [x] Add `StreamIdleTimeout` (TimeSpan, default 120s) to `SessionConfig`
- [x] Add `FirstTokenTimeoutSeconds` and `StreamIdleTimeoutSeconds` to `RawSessionConfig`
- [x] Update `BindFromConfiguration` with fallback logic: explicit value → TurnLlmTimeout → default
- [x] Add both properties to `netclaw-config.v1.schema.json`
- [x] Verify build passes

## Phase C: Wire two-phase into actor

- [x] Add `private bool _firstDeltaReceived;` field to `LlmSessionActor`
- [x] Reset `_firstDeltaReceived = false` in `HandleIncomingUserMessage`
- [x] Update `FireLlmCall()` to use `_config.FirstTokenTimeout` for watchdog start
  and `SessionLlmInvoker.InvokeAsync` timeout
- [x] Update `LlmResponseDeltaReceived` handler: always pass `_config.StreamIdleTimeout`
  to `_watchdog.Refresh()`
- [x] Add `TimeoutException` branch to `ExtractLlmErrorMessage`
- [x] Ensure `LlmCallFailed` handler classifies `TimeoutException` as `ErrorCategory.Timeout`
- [x] Verify build passes

## Phase D: Tests

- [x] Update `LlmSessionWatchdogTests` config to use `FirstTokenTimeout` and
  `StreamIdleTimeout` instead of `TurnLlmTimeout`
- [x] Update `ErrorCorrelationTests` config similarly
- [x] Fix any assertion changes due to new error messages
- [x] Create `LlmSessionTwoPhaseTimeoutTests.cs` with:
  - `First_token_timeout_fires_when_no_deltas_arrive`
  - `Stream_idle_timeout_fires_when_stream_stalls_after_deltas`
  - `Successful_stream_completes_without_timeout`
- [x] Full test suite passes
- [x] `dotnet slopwatch analyze` — no new violations
