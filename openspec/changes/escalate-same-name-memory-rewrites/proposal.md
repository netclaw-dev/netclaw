## Why

A same-name document rewrite can bypass the LLM after the overwrite guard rejects a direct update.
The `Create` result then reaches store deduplication and appends a second full copy.

Source PRDs: [PRD-007](../../../docs/prd/PRD-007-agent-personality-and-local-memory.md), product outcome 2.
Plan trace: `IMPLEMENTATION_PLAN.md`, Task 5.2.
Terms: [engineering glossary](../../../docs/spec/GLOSSARY.md).

## What Changes

- Keep an exact-name proposal `Ambiguous` after guard rejection when no safe deterministic decision resolves it.
- Route that proposal through the existing LLM decision tier before `Create` or store deduplication can append content.
- Preserve the overwrite guard, safe deterministic decisions, and different-name low-overlap `Create` decisions.
- Keep unresolved same-name proposals blocked when the LLM cannot supply a valid decision.
- Preserve the canonical `MemoryId`, content validation, and `merge-document` contract for accepted LLM updates.
- Reject malformed decision keywords and unknown candidate IDs before acceptance or persistent effects.
- Preflight both consumer batches before consolidation can change anchor state.
- Prevent a higher-confidence different-name candidate from bypassing unresolved same-name evidence.

MVP scope: a shared decision correction, consumer preflight, and focused tests.
Out of scope: a new LLM tier, configuration, schema, data format, record admission changes, or live data cleanup.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-agent-memory`: require exact-name guard fall-through to reach the existing LLM tier when deterministic evidence remains insufficient.

## Impact

The fix concerns `CurationRulesEvaluator`, `MemoryCurationEvaluator`, both consumer batch loops, and focused tests.
`SQLiteMemoryStore` remains the consumer of canonical write operations.
The canonical specification continues to distinguish mutable documents from immutable records and to require rules-first curation.

### Security and operational impact

Audience, boundary scope, and record admission remain unchanged.
Previously unresolved `Create` cases now require an additional call to the existing LLM tier.
These calls add token cost and curation latency.
An unavailable or invalid LLM decision must leave the proposal unresolved without a write.
This change introduces no alternate persistence route or silent fallback.
