## Why

GitHub issue #183 needs a smallest-safe MVP for subagent findings because the current specs say accepted findings can become durable checkpoints without defining which subagents may emit findings or how the parent session decides. Without an explicit contract, Netclaw risks treating speculative summaries or raw work logs like verified durable memory.

Source PRDs: `PRD-007` (primary), `PRD-001`, `PRD-002`

## What Changes

- Define an opt-in subagent findings contract so only selected subagents may return structured findings envelopes.
- Constrain findings envelopes to durable conclusion candidates plus review metadata, not raw work logs, tool transcripts, or step-by-step execution trace.
- Define a parent-session accept/defer/reject heuristic for subagent findings that is stricter than the simpler heuristic already used for verified tool findings.
- Clarify that accepted subagent findings become durable checkpoints only after parent-session review and that default behavior stays conservative and fail-closed.
- Split MVP-now scope from deferred follow-up work so richer scoring, reviewer UX, and broader auto-accept do not block the first safe implementation.

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `netclaw-subagents`: tighten which subagents may emit findings and define the structured findings envelope contract.
- `netclaw-agent-memory`: define conservative parent-session heuristics for deciding when subagent findings become durable checkpoints.
- `netclaw-session`: clarify checkpoint scheduling behavior for accepted, deferred, and rejected subagent findings.

## Impact

- Affected systems: subagent result contracts, parent session review flow, durable checkpoint enqueue path, and memory candidate extraction/policy checks.
- Security impact: preserves default-deny memory behavior by blocking broad subagent auto-accept and requiring parent-session review before any durable write path.
- Operational impact: adds deterministic acceptance scenarios and clearer failure behavior for ambiguous, low-confidence, or sensitive findings.
- In scope for MVP-now: opt-in findings-capable subagents, structured envelope metadata, conservative accept/defer/reject heuristics, and checkpoint scheduling behavior.
- Out of scope for MVP-now: weighted scoring systems, operator approval UX, long-lived deferred-review queues, adaptive heuristics, and broad auto-accept across most subagents.

### Traceability

- GitHub issue: `#183`
- Related capabilities: `openspec/specs/netclaw-subagents/spec.md`, `openspec/specs/netclaw-agent-memory/spec.md`, `openspec/specs/netclaw-session/spec.md`
