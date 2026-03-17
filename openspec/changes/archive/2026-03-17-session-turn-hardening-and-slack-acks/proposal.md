## Why

Recent fixes reduced some permanent session hangs, but production behavior still shows turns that either fail after long hidden tool loops, emit duplicate Slack fallback warnings after a real reply, or recover poorly across timeout and restart boundaries. We need a tighter turn-state contract so Netclaw can prove a turn is still making progress, fail durably when it cannot complete, and deliver one clear user-visible outcome instead of silence or transport-shaped confusion.

Source PRDs: `PRD-001` (primary), `PRD-009`

## What Changes

- Harden the session turn state machine so stale LLM and tool completions cannot mutate a newer turn after timeout, retry, or restart.
- Persist accepted-turn failure outcomes and buffered follow-up inputs so restart and replay behavior stays deterministic instead of depending on transient in-memory state.
- Add an absolute wall-clock turn budget and define degraded completion behavior after tool-budget exhaustion so long-running turns still end with a usable answer, clarifying question, or explicit timeout outcome.
- Let the Slack adapter observe hidden tool-call activity without rendering raw tool events, and post a single lightweight in-thread acknowledgement when a turn is clearly working but still silent.
- Fix Slack empty-turn fallback behavior so streamed or buffered replies do not trigger a second generic warning after the real reply is already posted.
- Keep MVP-now scope focused on turn reliability and transport presentation; broader multi-channel progress UX and richer loop-detection heuristics remain follow-up work.

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `netclaw-session`: define stale-completion isolation, durable failed-turn recovery, buffered replay recovery, absolute turn budgets, and degraded completion after tool-budget exhaustion.
- `netclaw-input-adapters`: clarify that adapters may subscribe to hidden tool activity for progress heuristics without coupling session behavior to a specific transport.
- `netclaw-slack-socket`: define one-shot Slack acknowledgement behavior for hidden work and tighten empty-turn fallback rules.

## Impact

- Affected systems: `LlmSessionActor`, session persistence/recovery, `SlackThreadBindingActor`, session output filters, and turn/retry telemetry.
- Security impact: preserves default-deny behavior and does not expose hidden tool arguments or tool results to Slack users.
- Operational impact: adds clearer terminal outcomes, restart-safe failure handling, and deterministic progress signals for long-running Slack turns.
- In scope for MVP-now: turn-operation correlation, failed-turn durability, buffered follow-up recovery, absolute turn budget enforcement, degraded tool-budget completion, Slack hidden-work acknowledgement, and duplicate-fallback suppression.
- Out of scope for MVP-now: generalized cross-channel progress UI contracts, adaptive acknowledgement phrasing, broad loop-scoring systems, and non-Slack transport-specific acknowledgements.

### Traceability

- Related capabilities: `openspec/specs/netclaw-session/spec.md`, `openspec/specs/netclaw-input-adapters/spec.md`, `openspec/specs/netclaw-slack-socket/spec.md`
