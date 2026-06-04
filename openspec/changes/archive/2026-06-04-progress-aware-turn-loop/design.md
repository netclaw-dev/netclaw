## Context

The session turn loop currently uses `SessionConfig.MaxToolCallsPerTurn` to cap
work inside one user turn. That limit counts individual tool calls, which makes
parallel tool batches look artificially expensive. The behavior we actually need
to control is the number of times the loop cycles between the model and tool
execution before a final answer is produced.

## Goals / Non-Goals

**Goals:**

- Stop indefinite turn-local spinning by bounding LLM-to-tools-to-LLM
  iterations.
- Stop penalizing productive turns for issuing many tool calls in one parallel
  batch.
- Keep the existing end-of-turn behavior once the cap is reached.

**Non-Goals:**

- No progress or productivity classification.
- No model-facing advisory or checkpoint guidance.
- No redesign of graceful wrap-up behavior.
- No partial-delivery or synthetic closed-tool recovery work.
- No secondary emergency circuit breaker.
- No other turn lifecycle changes.

## Decisions

### Decision 1: Replace tool-call counting with iteration counting

`SessionConfig.MaxToolCallsPerTurn` is replaced by
`MaxToolIterationsPerTurn`.

A **tool iteration** is one completed model-to-tools round inside a single user
turn:

- the model produces a response requesting one or more tool calls,
- those tool calls execute,
- their results are returned to the model.

One LLM response with many parallel tool calls counts as **one** iteration.

This aligns the guardrail with the actual loop structure and removes the
current penalty against parallel work.

### Decision 2: Keep the existing turn-ending path

When `MaxToolIterationsPerTurn` is reached, the session uses the existing
force-no-tools summary path to end the turn. No new wrap-up protocol is
introduced by this change.

### Decision 3: Keep all other lifecycle behavior unchanged

This change only swaps the governing counter:

- iteration count becomes the enforcement input,
- raw tool-call count is no longer the governing limit,
- no new intermediate states, advisories, or recovery flows are added.

## Config

- Remove `MaxToolCallsPerTurn`.
- Add `MaxToolIterationsPerTurn`.
- Update binding, defaults, and JSON schema together in the same change.

## Risks / Trade-offs

- A turn can still make many total tool calls if those calls are grouped into a
  small number of iterations; this is acceptable because the target problem is
  endless looping, not raw call volume.
- A turn that loops with small parallel batches is still bounded because each
  loop consumes one iteration.
- Existing behavior after limit exhaustion remains intact, which keeps the
  change small but does not improve the wrap-up UX.

## Migration Plan

Single-change migration:

- add `MaxToolIterationsPerTurn`,
- remove `MaxToolCallsPerTurn`,
- update the schema so old configs are rejected as stale and new configs
  validate against the renamed property.
