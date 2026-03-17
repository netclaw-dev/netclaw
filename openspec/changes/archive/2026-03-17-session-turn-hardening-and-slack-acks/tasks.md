## 1. Session turn ownership and recovery

- [x] 1.1 Add turn-operation correlation for LLM and tool work so stale completions are ignored after timeout, retry, or replay.
- [x] 1.2 Persist failed-turn outcomes and accepted buffered follow-up inputs, then recover and replay them once in original order after restart.
- [x] 1.3 Add actor-level tests covering stale completion isolation and restart-safe buffered replay / failed-turn recovery.

## 2. Turn budgets and degraded completion

- [x] 2.1 Add a cumulative wall-clock turn budget and enforce a deterministic timeout outcome when the budget is exceeded.
- [x] 2.2 Change tool-budget exhaustion to a degraded answer-or-ask completion path, with a deterministic fallback if the model still attempts more tool work.
- [x] 2.3 Update session tests for tool-budget exhaustion, timeout behavior, and terminal-output guarantees.

## 3. Slack hidden-work acknowledgement

- [x] 3.1 Widen the Slack session subscription to observe hidden tool activity while continuing to suppress raw tool-call and tool-result rendering.
- [x] 3.2 Post one lightweight Slack acknowledgement after the hidden-activity threshold is reached and no visible reply has been delivered yet.
- [x] 3.3 Separate acknowledgement tracking from terminal output tracking so real replies, files, and explicit errors suppress duplicate empty-turn fallback warnings.
- [x] 3.4 Add Slack adapter tests covering acknowledgement thresholds, fast-turn no-ack behavior, and duplicate-fallback suppression.

## 4. Docs and validation

- [x] 4.1 Update implementation-facing docs and configuration docs for the new turn-budget and degraded-completion semantics.
- [x] 4.2 Validate the change with `openspec validate --change session-turn-hardening-and-slack-acks --strict`, targeted `dotnet test` coverage, and `dotnet slopwatch analyze`.
