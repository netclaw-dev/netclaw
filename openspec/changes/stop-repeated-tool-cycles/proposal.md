## Why

PRD-001 and PRD-006 require reliable long agent turns and factual tool results.

The current iteration cap stops all long turns, but it does not stop an active tool cycle early.

## What Changes

- Add a bounded detector for exact action-and-outcome cycles with periods one through three.
- Block the next batch before execution after two complete copies of a cycle.
- Return one paired correction result for the first blocked batch.
- Force a text-only response when the model repeats the blocked batch.
- Reset detector state for a new user turn, but preserve it across normal compaction.
- Preserve loaded deferred schemas across successful normal compaction.
- Evict loaded schemas after recovery or an LLM failure, including context overflow.
- Give an MCP tool-declared error a non-success receipt without parsing its text.
- Use the same detector contract for parent and child actors.
- Keep the parent and child iteration limits as rollout guards until replay evidence passes.
- Remove both iteration limits only after the staged acceptance gates pass.
- Add aggregate diagnostics that contain the cycle period, repetition count, and decision.
- Keep arguments, results, identifiers, session data, and hashes out of logs.

In scope:

- Exact comparison of canonical execution arguments and exact model-visible results.
- Complete parallel batches, cancellation, approval redrive, compaction, and call-result pair integrity.
- A disposable standalone detector laboratory and sanitized fixtures before runtime integration.
- Observe-only rollout before the correction and final-stop stages.

Out of scope:

- A semantic progress score, embeddings, or an LLM judge.
- Provider-specific decoder changes or model-family settings.
- Durable detector state or cross-turn penalties.
- Tool-specific command grammar or arbitrary result normalization.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `turn-loop-governance`: Detect exact completed cycles and define correction, stop, reset, and rollout behavior.
- `progressive-tool-disclosure`: Preserve loaded schemas across normal compaction and evict them after failures or recovery.
- `netclaw-tools`: Classify tool-declared MCP failures and preserve paired synthetic correction results.
- `netclaw-subagents`: Apply the same cycle contract and staged limit removal to child runs.

## Impact

The change affects `TurnStateTracker`, both actor tool loops, tool-call preparation, result receipts, and compaction state transitions.

The change adds no package, provider, public API, actor, persistence event, or configuration property.

The detector state is actor-local and bounded to six completed iterations.

The correction grants no authority and causes no requested side effect.

Operators receive aggregate cycle decisions without sensitive payloads.
