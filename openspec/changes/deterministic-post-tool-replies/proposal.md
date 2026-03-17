## Why

Recent turn-hardening work fixed tool-budget exhaustion and several empty-turn edge cases, but one core failure mode remains: the model can complete multiple successful tool iterations, then return repeated empty post-tool responses and cause the session actor to emit a generic provider failure. We need the session actor to end that turn with a deterministic, best-effort reply when recent tool evidence is usable, while preserving generic failure only for cases where the turn truly has nothing reliable to show the user.

Source PRDs: `PRD-001` (primary), `PRD-009`

## What Changes

- Extend session turn-finalization behavior so repeated empty post-tool completions are treated as a degraded completion path instead of an automatic provider failure.
- Track bounded, turn-local evidence from successful tool results so the session actor can synthesize a deterministic best-effort user-visible answer when the model refuses to produce final text after tool work.
- Keep the synthesized outcome transport-agnostic: the session emits an ordinary terminal text reply, and adapters such as Slack only render that existing session output.
- Preserve generic provider failure when the post-tool empty-response path has no usable evidence to synthesize from.
- Keep MVP scope focused on session correctness and deterministic terminal outcomes; broader adapter UX changes, richer evidence scoring, and model-driven fallback summarization remain follow-up work.

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `netclaw-session`: add deterministic evidence-backed completion for persistent post-tool empty responses and define when generic provider failure remains valid.

## Impact

- Affected systems: `LlmSessionActor`, turn-finalization state, tool-result capture, terminal reply synthesis, and actor/integration tests around empty post-tool completions.
- Security impact: preserves transport-agnostic session behavior and reuses existing policy-gated tool evidence rather than introducing a new adapter-specific bypass.
- Operational impact: reduces false generic provider failures after successful tool work and gives subscribers a deterministic visible outcome when the session has enough evidence to answer.
- Slack-visible effect: existing adapters receive a normal text terminal output from the session, so no Slack-owned synthesis logic is required.
- Out of scope: new Slack acknowledgement rules, generalized progress heuristics, cross-turn evidence persistence, and LLM-authored fallback summarization after the bounded retry path is exhausted.
