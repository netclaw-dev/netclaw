## Context

The current session actor already survives several turn-failure modes: stale completions are isolated, tool-budget exhaustion degrades to a deterministic terminal path, and accepted failures are recorded durably. The remaining gap happens later in the turn. After one or more successful tool iterations, the provider can return an empty completion with no tool calls and no assistant text. When that persists across the session's existing finalization attempts, the turn currently falls into the generic provider-failure path even though the actor has recent successful tool evidence that could support a useful answer.

This is primarily a session-actor correctness problem, not a Slack problem. The actor owns tool execution, turn-finalization policy, and terminal outputs. Adapters should only observe the resulting `TextOutput` or `ErrorOutput`. That boundary must stay intact so Slack-visible improvement comes from a transport-agnostic session outcome rather than new Slack-owned recovery logic.

Source PRDs: `PRD-001` (primary), `PRD-009`

## Goals / Non-Goals

**Goals:**
- Detect the specific degraded path where post-tool completion attempts stay empty even though the turn has successful recent tool evidence.
- Track bounded, deterministic evidence from the active turn so fallback synthesis does not depend on another model call.
- Emit a normal terminal session text reply when evidence is usable, allowing existing adapters to render it without transport-specific branching.
- Preserve generic provider failure when the actor cannot derive a trustworthy best-effort answer from the current turn's evidence.
- Keep the implementation compatible with the existing persisted turn lifecycle and terminal-output contract.

**Non-Goals:**
- No new Slack-specific synthesis or fallback logic.
- No cross-turn or restart-persistent evidence ledger for this change.
- No free-form LLM summarization pass after the bounded empty-response retries are exhausted.
- No expansion of acknowledgement heuristics, progress UX, or tool-budget policy beyond this failure mode.

## Decisions

### Decision: Track a bounded turn-local evidence ledger from successful tool results

The session actor will capture a small, ordered set of usable evidence records from successful tool results during the active turn. Each record should preserve enough deterministic detail to support a fallback answer: tool identity, success state, and a bounded textual evidence excerpt or normalized summary already available from the tool result.

Rationale: once the provider starts returning empty post-tool completions, the actor needs its own reliable substrate for a best-effort answer. Re-reading raw tool payloads ad hoc would be brittle and could produce unstable output ordering.

Alternative considered: inspect the full conversation history only at fallback time. Rejected because the actor would need to rediscover which tool outputs were successful and usable, making fallback behavior harder to reason about and test.

### Decision: Treat persistent empty post-tool completions as a distinct degraded-finalization state

The actor will explicitly classify completions that arrive after tool work but contain no assistant text, no file output, and no additional tool calls. A bounded retry counter will distinguish a transient empty completion from a persistent post-tool empty-response failure mode.

Rationale: this failure is materially different from a provider exception, timeout, or tool failure. The provider completed successfully, but the turn still lacks a user-visible answer.

Alternative considered: keep routing these cases through the generic provider-failure path. Rejected because it throws away usable evidence and misclassifies a degraded finalization problem as provider failure.

### Decision: Synthesize the fallback reply deterministically in code, not with another model call

Once the bounded empty-response threshold is reached and usable evidence exists, the actor will build a transport-agnostic best-effort reply from the evidence ledger using a fixed formatting strategy. The fallback text should clearly signal that it is a best-effort answer derived from completed tool work, while still surfacing the most relevant findings first.

Rationale: deterministic synthesis guarantees a user-visible outcome even when the provider keeps returning empty completions. It also avoids re-entering the same failure mode with yet another model request.

Alternative considered: make one last LLM call with a stricter prompt to summarize the evidence. Rejected because the problem is specifically that the provider has already failed to produce final text after tool work.

### Decision: Keep adapter-visible behavior as an effect of ordinary session outputs

The synthesized fallback reply will be emitted and persisted through the existing completed-turn pathway as ordinary session text. Adapters, including Slack, should treat it exactly like any other terminal reply and should not receive a new transport-specific recovery signal.

Rationale: the user-visible behavior belongs to the session outcome contract. Existing adapter logic already knows how to render visible text and suppress generic empty-turn fallback once a real reply exists.

Alternative considered: add a new adapter-specific fallback output type for Slack. Rejected because it would duplicate session logic at the transport boundary and weaken the actor contract.

## Risks / Trade-offs

- [Risk] Tool evidence can be noisy or only partially relevant, leading to a rough fallback answer. -> Mitigation: keep the evidence ledger bounded to successful, recent, text-usable results and prefer stable ordering over aggressive synthesis.
- [Risk] Empty-response detection could catch legitimate but rare provider behaviors too early. -> Mitigation: require bounded repeated empty post-tool completions before synthesis and cover the threshold behavior with actor tests.
- [Risk] Deterministic fallback wording may feel less polished than model-authored text. -> Mitigation: reserve it for the persistent empty-response edge case and make the message explicitly best-effort.
- [Risk] Turn-local evidence is not restart-persistent in this slice. -> Mitigation: keep scope focused on the active failure mode and preserve existing durable failed-turn handling for restart safety.

## Migration Plan

1. Add an OpenSpec delta for `netclaw-session` covering persistent post-tool empty responses and evidence-backed completion.
2. Extend `LlmSessionActor` turn state to capture bounded successful tool evidence and count post-tool empty finalization attempts.
3. Emit a deterministic synthesized terminal reply when the empty-response threshold is exceeded and usable evidence exists; otherwise preserve the generic provider-failure path.
4. Add actor and integration tests for evidence-backed completion, no-evidence failure, and adapter-visible behavior through ordinary session text outputs.
5. Validate with `openspec validate --change deterministic-post-tool-replies --strict` and the relevant `dotnet test` / `dotnet slopwatch analyze` checks during implementation.

Rollback does not require a journal migration. The change stays within turn-processing logic and existing terminal output types.

## Open Questions

- What is the smallest useful evidence excerpt shape for fallback synthesis without over-exposing noisy raw tool output?
- Should the bounded empty-response threshold reuse existing no-tools recovery attempts or be tracked as a dedicated counter?
