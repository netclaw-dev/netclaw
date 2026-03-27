## Why

The current `TurnLlmTimeout` (default 3 minutes) is a single wall-clock timer that
conflates two fundamentally different concerns: waiting for the model to complete
prefill on a large context (which can legitimately take 5-10 minutes), and detecting
a dead stream once tokens are flowing (which should fire within 1-2 minutes of
silence). This causes Netclaw to kill legitimate slow requests on large contexts,
and the retry mechanism we prototyped (#447) just resubmits the same expensive call
against a provider that may be alive but slow — wasting inference time and API cost.

Industry consensus (Anthropic SDK [#867](https://github.com/anthropics/anthropic-sdk-typescript/issues/867),
Claude Code [#6781](https://github.com/anthropics/claude-code/issues/6781),
[#18028](https://github.com/anthropics/claude-code/issues/18028), Claude Agent SDK
[#44](https://github.com/anthropics/claude-agent-sdk-typescript/issues/44)) is to use
a **stream-idle timeout** that resets on every SSE chunk, separate from the initial
first-token wait. Our watchdog already refreshes on deltas — we just need to split
the timeout into two phases.

This also reverts the retry-with-backoff mechanism from the current PR, which was
a wrong-level solution to this problem.

## What Changes

- **BREAKING**: Remove `LlmTimeoutMaxRetries` and `LlmTimeoutRetryBaseDelaySeconds`
  config properties (introduced in current PR, not yet released)
- Add `FirstTokenTimeoutSeconds` config (default 600s / 10 minutes) — generous
  ceiling for prefill + initial inference
- Add `StreamIdleTimeoutSeconds` config (default 120s / 2 minutes) — tight timeout
  that resets on every streaming delta
- Watchdog starts with first-token timeout, switches to stream-idle on first delta
- `SessionLlmInvoker` CTS uses first-token timeout as hard ceiling
- Remove `TimeoutRetryHandler`, `RetryLlmCallAfterBackoff` message, and all retry
  wiring from `LlmSessionActor`
- Backward compat: existing `TurnLlmTimeoutSeconds` config continues to work (used
  for both phases if new properties are not set)

## Capabilities

### New Capabilities

_None — this modifies existing session timeout behavior._

### Modified Capabilities

- `netclaw-session`: The "LLM call timeout produces LlmCallFailed" and "Streaming
  deltas forwarded to actor" requirements change. Timeout is now two-phase
  (first-token vs stream-idle) instead of a single wall-clock deadline. The watchdog
  refresh behavior changes from using the same timeout value to using the
  stream-idle value after first delta.

## Impact

- **Config**: New `FirstTokenTimeoutSeconds` and `StreamIdleTimeoutSeconds` in
  `SessionConfig` + JSON schema. Removal of `LlmTimeoutMaxRetries` and
  `LlmTimeoutRetryBaseDelaySeconds`.
- **Actor**: `LlmSessionActor` gains `_firstDeltaReceived` field, loses retry handler
  and timer key. `ProcessingWatchdogExpired` handler simplifies.
- **Invoker**: `SessionLlmInvoker.InvokeAsync` receives first-token timeout.
- **Tests**: Delete retry test files, update watchdog/correlation test configs, add
  two-phase timeout integration test.
- **No wire-format or persistence changes** — this is purely runtime behavior.
