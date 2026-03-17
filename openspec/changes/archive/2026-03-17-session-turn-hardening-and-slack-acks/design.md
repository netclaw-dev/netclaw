## Context

Recent production sessions showed three reliability gaps at once: hidden tool loops that kept Slack silent until the turn failed, duplicate Slack fallback warnings after a real streamed reply was already posted, and session turns whose timeout or restart recovery still depended on transient in-memory state. The current `LlmSessionActor` already tracks channel type for telemetry and already emits typed `SessionOutput` events, but it does not fully correlate late async completions to the currently active turn, and the Slack adapter only subscribes to text and file outputs.

This change crosses two actor boundaries. The session actor owns correctness: active-turn ownership, persistence, timeout behavior, and deterministic terminal outcomes. The Slack thread binding actor owns presentation: whether hidden activity should produce a user-visible acknowledgement and whether a fallback message is still warranted after visible output. The design must keep those responsibilities separate so long-running turns become safer without teaching the LLM transport-specific UX rules.

Source PRDs: `PRD-001` (primary), `PRD-009`

## Goals / Non-Goals

**Goals:**
- Make turn ownership explicit so late LLM or tool completions cannot mutate a newer turn.
- Persist failure and buffered follow-up state needed to recover accepted turns safely after restart.
- Enforce an absolute wall-clock budget for a turn across multiple LLM and tool iterations.
- End tool-budget exhaustion with a degraded but user-visible completion instead of a silent or misleading provider failure.
- Let the Slack adapter observe hidden tool activity and emit one lightweight acknowledgement without leaking raw tool details.
- Eliminate duplicate Slack empty-turn fallback warnings after a real reply, file, or explicit error was already delivered.

**Non-Goals:**
- No transport-specific prompting or Slack-aware instructions inside the LLM system prompt.
- No broad new progress protocol for every adapter in this MVP slice.
- No adaptive acknowledgement phrasing, multiple staged acknowledgements, or rich progress percentages.
- No comprehensive loop-scoring engine beyond the existing tool-iteration budget and new wall-clock turn budget.
- No production rollout gating beyond tests, OpenSpec validation, and existing diagnostics.

## Decisions

### Decision: Correlate all async completions to the active turn operation

The session actor will assign turn-scoped operation identifiers to each LLM request and tool-execution batch. Completion, failure, timeout, and streaming messages must carry that identifier, and the actor will ignore messages whose identifier no longer matches the active operation.

Rationale: inactivity watchdogs alone do not prevent late continuations from mutating the wrong turn after timeout, retry, or buffered replay. Explicit correlation makes stale completions safe to ignore without relying on timing assumptions.

Alternative considered: rely on behavior transitions and cancellation tokens only. Rejected because the existing actor already demonstrates that async tasks can outlive the state in which they were started.

### Decision: Persist failed-turn and buffered follow-up recovery state

Accepted turns will gain explicit durable failure events, and buffered follow-up user inputs that are accepted while a turn is processing will be persisted in arrival order. Recovery rebuilds the pending queue and the last terminal state before processing resumes.

Rationale: the current `_buffer` and failed-turn handling are in-memory only, so daemon restart can lose accepted follow-up inputs or resurrect turns in inconsistent states.

Alternative considered: leave buffering transient and treat restart as best-effort during active work. Rejected because the reported failures are specifically about trust in the state machine after long-running or stuck turns.

### Decision: Add an absolute wall-clock turn budget above per-operation watchdogs

The session actor will track total elapsed time from turn acceptance until terminal completion. If the turn exceeds a configured ceiling, it aborts further LLM/tool work, records a timeout outcome, and advances the buffered queue.

Rationale: the current watchdog is activity-based, so a stream or tool chain that keeps making small progress can still live too long and leave the user waiting indefinitely.

Alternative considered: lower only the existing LLM/tool timeout values. Rejected because that bounds individual operations, not the full multi-iteration turn.

### Decision: Tool-budget exhaustion degrades to answer-or-ask, then deterministic fallback

The tool-iteration limit remains as a safety circuit breaker, but exhausting it no longer ends as a generic provider failure. The actor will first force a no-tools completion path aimed at producing a best-effort answer or one focused clarifying question. If the model still refuses to comply, the actor emits a deterministic degraded terminal message rather than leaving the turn empty.

Rationale: the user-visible contract should be “I either answered, asked one question, or timed out clearly,” not “the model looked busy and then produced a transport-shaped error.”

Alternative considered: remove the tool limit entirely. Rejected because earlier incidents showed genuine runaway tool loops.

### Decision: Slack subscribes to hidden tool activity but keeps rendering policy local

`SlackThreadBindingActor` will widen its session subscription to include `ToolCalls`, count hidden tool activity per turn, and post a one-shot acknowledgement such as `Working on it.` after a configurable threshold. Tool-call and tool-result details remain suppressed from the Slack thread.

Rationale: Slack is the transport experiencing the silence problem, and the session actor already emits tool activity through transport-agnostic outputs. This keeps UX policy in the adapter while reusing the existing pub/sub contract.

Alternative considered: add a new session-level progress output for all channels first. Rejected for MVP because Slack can solve the immediate UX issue by observing outputs that already exist.

### Decision: Track acknowledgement separately from terminal output in Slack

The Slack adapter will distinguish between an acknowledgement message and a terminal turn outcome. Acknowledgements do not suppress later fallback or error delivery, but any real reply, file upload, or explicit error does suppress the generic empty-turn fallback.

Rationale: the current `_postedThisTurn` flag conflates “any Slack message was posted” with “the turn produced a terminal visible outcome,” which is how duplicate warnings slipped through.

Alternative considered: keep a single posted flag and treat acknowledgement as terminal output. Rejected because acknowledgement-only turns still need a clear final outcome.

## Risks / Trade-offs

- [Risk] More persistence events and snapshot fields make recovery code more complex. -> Mitigation: keep new event types narrowly scoped to failed turns and pending buffered inputs, and add restart-focused actor tests.
- [Risk] An acknowledgement threshold that is too low could create Slack noise on normal research turns. -> Mitigation: start with a conservative one-shot threshold and cover the fast-turn no-ack path in tests.
- [Risk] Absolute turn budgets can stop legitimate long-running work too early. -> Mitigation: make the budget configurable and prefer a clear degraded timeout outcome over silent waiting.
- [Risk] Degraded completion text may feel generic when the model refuses to answer after tool exhaustion. -> Mitigation: reserve deterministic fallback for noncompliant edge cases and keep the preferred path as a no-tools answer-or-ask completion.

## Migration Plan

1. Update OpenSpec deltas for `netclaw-session`, `netclaw-input-adapters`, and `netclaw-slack-socket`.
2. Add session actor correlation IDs, failed-turn persistence, buffered input recovery, absolute turn-budget enforcement, and degraded tool-budget completion.
3. Update the Slack adapter subscription and per-turn state tracking for hidden-activity acknowledgements and duplicate-fallback suppression.
4. Add actor/integration tests for stale completion rejection, restart recovery, degraded turn completion, and Slack acknowledgement/fallback behavior.
5. Validate with targeted tests, `openspec validate --change session-turn-hardening-and-slack-acks --strict`, and `dotnet slopwatch analyze`.

Rollback does not require data migration. New event handling is additive and should default safely when replaying old sessions without the new events.

## Open Questions

- Should the absolute turn budget be a new session configuration field or derived from the existing per-operation timeouts in MVP?
- Is the Slack acknowledgement threshold best expressed only as hidden tool-call count, or should a future follow-up add elapsed-time heuristics too?
- Should the deterministic degraded message after repeated no-tools noncompliance be configurable, or is a fixed built-in message sufficient for MVP?
