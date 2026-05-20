## Why

The current `MaxToolCallsPerTurn` limit is the wrong control surface for long
productive turns. It counts raw tool calls, so a single LLM response that
launches a large parallel batch burns budget much faster than a turn that uses
the same number of serial round-trips. That kneecaps legitimate sessions without
directly targeting the actual failure mode: a turn that keeps looping between
the model and tools without ever finishing.

## What Changes

- **BREAKING** - replace `SessionConfig.MaxToolCallsPerTurn` with
  `MaxToolIterationsPerTurn`.
- Govern the turn loop by counting LLM-to-tools-to-LLM rounds, not individual
  tool calls.
- Count one LLM response containing any number of parallel tool calls as exactly
  one iteration.
- When the iteration cap is reached, use the existing force-no-tools summary
  behavior to end the turn.
- Keep the rest of the turn lifecycle unchanged.

## Capabilities

### New Capabilities

- `turn-loop-governance`: specifies that per-turn loop limiting is based on
  tool iterations rather than raw tool-call count.

### Modified Capabilities

- `session-config-decomposition`: replaces `MaxToolCallsPerTurn` with
  `MaxToolIterationsPerTurn` on the session config surface and schema.

## Impact

- **Source PRDs**: PRD-001 FR-002, FR-011.
- **Code**: session turn-loop accounting and config binding.
- **Config / ops**: schema and docs update for the renamed session setting.
- **No new loop heuristics**: no progress classification, advisory text,
  wrap-up redesign, or additional circuit breaker.
