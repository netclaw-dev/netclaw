## Design: Two-Phase Streaming Timeout

### Architecture

Two independent timeout phases protect LLM streaming calls:

```
FireLlmCall()
  │
  ├─ CTS(firstTokenTimeout) ─── hard ceiling on entire call
  │
  ├─ Watchdog.Start("llm-call", firstTokenTimeout)
  │          │
  │    [waiting for first token — prefill phase]
  │          │
  │    LlmResponseDeltaReceived ──→ _firstDeltaReceived = true
  │          │                       Watchdog.Refresh(streamIdleTimeout) ← PHASE SWITCH
  │          │
  │    LlmResponseDeltaReceived ──→ Watchdog.Refresh(streamIdleTimeout)
  │    LlmResponseDeltaReceived ──→ Watchdog.Refresh(streamIdleTimeout)
  │    ...
  │          │
  │    LlmResponseReceived ──→ success
  │
  └─ [stream silence > streamIdleTimeout] ──→ ProcessingWatchdogExpired ──→ FailCurrentTurn
```

### Key Decisions

**1. CTS timeout = first-token timeout (not infinite)**

The `CancellationTokenSource` in `SessionLlmInvoker` cannot be refreshed — it's an
absolute wall-clock timer. We set it to `FirstTokenTimeout` (10 min default). This
provides a hard ceiling at the stream level. The watchdog handles the stream-idle
phase at the actor level. A response that actively streams for >10 minutes is covered
because the CTS is only the initial call ceiling — once streaming starts, the
watchdog takes over.

**2. No retry at session level**

Retry was removed because:
- If the provider is alive but slow → the generous first-token timeout lets it finish
- If the provider is dead → retrying against a dead provider wastes time
- Transient HTTP errors → already handled by `RetryingChatClient` at transport layer

**3. Phase switch on first delta (not second)**

The actor receives `LlmResponseDeltaReceived` starting from the second provider
delta (first is withheld by `SessionLlmInvoker.StreamAsync`). This is fine — by the
time the actor sees ANY delta, the stream is actively producing tokens. The brief gap
between provider's first and second delta is covered by the still-running first-token
timeout.

**4. Backward compatibility via `TurnLlmTimeout` fallback**

If an operator has `TurnLlmTimeoutSeconds` configured but not the new properties,
both `FirstTokenTimeout` and `StreamIdleTimeout` fall back to `TurnLlmTimeout`.
This preserves exact current behavior for existing deployments.

### Config

```json
{
  "Session": {
    "TurnLlmTimeoutSeconds": 180,
    "FirstTokenTimeoutSeconds": 600,
    "StreamIdleTimeoutSeconds": 120
  }
}
```

Resolution order:
- `FirstTokenTimeout`: explicit value → `TurnLlmTimeout` fallback → 600s default
- `StreamIdleTimeout`: explicit value → `TurnLlmTimeout` fallback → 120s default

### Files Modified

| File | Change |
|------|--------|
| `SessionConfig.cs` | Add `FirstTokenTimeout`, `StreamIdleTimeout`. Remove retry props. |
| `netclaw-config.v1.schema.json` | Schema sync |
| `LlmSessionActor.cs` | Add `_firstDeltaReceived`. Remove retry handler/wiring. Update delta handler and `FireLlmCall`. |
| `LlmMessages.cs` | Remove `RetryLlmCallAfterBackoff` |
| `TimeoutRetryHandler.cs` | **Delete** |
| `SessionLlmInvoker.cs` | Receives `firstTokenTimeout` (no code change needed — already parameterized) |

### Error Messages

| Scenario | Message |
|----------|---------|
| First-token timeout | `"The LLM provider did not respond in time. The model may be overloaded or the context too large. Please try again."` |
| Stream-idle timeout | `"The LLM response stream stopped unexpectedly. Please try again."` |
| Connection error | Existing `ExtractLlmErrorMessage` handling |
