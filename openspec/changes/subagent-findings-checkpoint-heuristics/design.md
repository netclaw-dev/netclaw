## Context

GitHub issue #183 asks for the smallest-safe MVP for turning subagent findings into durable memory checkpoints. Netclaw already distinguishes verified tool findings from subagent findings at a high level, but the current specs do not yet define which subagents may emit findings, what a findings envelope must contain, or how the parent session decides whether a finding is durable enough to enqueue.

This planning slice touches three actor boundaries: the ephemeral `SubAgentActor`, the owning session actor for the Slack thread, and the durable memory checkpoint pipeline. The change must stay safer than permissive: subagents remain unable to write durable memory directly, the parent session remains the only durable-memory owner, and ambiguous findings must not silently become checkpoints.

Source PRDs: `PRD-007` (primary), `PRD-001`, `PRD-002`

## Goals / Non-Goals

**Goals:**
- Define an MVP findings envelope contract that only selected subagents may use.
- Require findings to represent durable conclusions rather than raw work logs or unfiltered tool output.
- Define a deterministic parent-session accept/defer/reject heuristic for subagent findings.
- Keep subagent findings stricter than simpler verified-tool checkpoint heuristics.
- Make persistence and recovery behavior explicit: only accepted findings become durable checkpoints.

**Non-Goals:**
- No direct durable-memory writes from subagents.
- No broad auto-accept across all subagents or all findings classes.
- No operator review UI, human approval workflow, or long-lived deferred-review queue.
- No weighted ML scoring, adaptive heuristics, or domain-specific tuning beyond conservative fixed defaults.
- No production implementation in this change.

## Decisions

### Decision: Findings emission is explicit opt-in per subagent definition

MVP-now adds a findings-capable flag or equivalent definition property on subagents. Only subagents explicitly marked for findings emission may return a structured findings envelope; all others return text output only.

Rationale: this keeps default behavior safer than permissive and avoids silently broadening durable-memory candidates across every helper agent.

Alternative considered: allow all subagents to emit findings and rely on parent review alone. Rejected because it creates noisy inputs, larger review surface area, and a higher risk of accidental memory promotion.

### Decision: Findings envelopes contain durable conclusion candidates, not work logs

Each envelope candidate is a conclusion-level claim the parent session can review for checkpointing. MVP-now requires metadata that supports deterministic review: candidate summary, provenance/evidence references, suggested `domain`, `sensitivity`, `confidence`, `durability`, and `reusability`. Raw transcripts, step-by-step notes, and execution breadcrumbs are not valid findings candidates.

Rationale: durable memory needs stable conclusions, while work logs belong in transient session history or diagnostics.

Alternative considered: allow free-form findings text and let the memory pipeline interpret it later. Rejected because it pushes ambiguity downstream and makes acceptance behavior hard to test.

### Decision: Parent session applies conservative accept/defer/reject heuristics

The parent session remains the authority for durable-memory promotion. MVP-now uses deterministic review outcomes per candidate:
- `accept`: allowed source subagent, complete metadata, policy-allowed domain/sensitivity, high enough confidence, durable enough beyond the current task, and reusable beyond a one-off execution detail.
- `defer`: potentially useful but incomplete, ambiguous, medium-confidence, or insufficiently durable/reusable for automatic checkpointing.
- `reject`: policy-denied, raw-log shaped, clearly ephemeral, or emitted by a subagent that is not findings-capable.

The default outcome is `defer` unless the candidate clearly satisfies the acceptance profile.

Rationale: subagent findings are synthesized judgments, so they need stricter review than verified tool facts.

Alternative considered: binary accept/reject only. Rejected because a defer state better captures conservative MVP behavior without forcing weak candidates into durable memory or discarding every borderline result.

### Decision: Verified tool findings keep the simpler heuristic boundary

MVP-now preserves a distinction between two persistence paths:
- verified tool findings can continue using the simpler existing checkpoint heuristic because their provenance is a direct tool result or verified artifact;
- subagent findings use the stricter review path because they are summarized, interpreted, or aggregated by another agent.

Rationale: the issue is specifically about when subagent findings deserve durable memory compared to simpler tool-call persistence heuristics.

Alternative considered: force all findings sources through the same strict heuristic. Rejected for MVP because it would broaden scope and risk unnecessary churn in already simpler verified-tool behavior.

### Decision: Only accepted findings become durable checkpoints

Accepted findings are converted into normal session-owned durable checkpoints and inherit existing retry/recovery behavior. Deferred and rejected findings are not persisted as durable checkpoints in MVP-now; they remain transient to the parent session turn and may be surfaced in diagnostics later if that is added in a follow-up change.

Rationale: this preserves durable-memory ownership and restart behavior without inventing a second persistence queue for deferred review.

Alternative considered: persist deferred findings into a separate review queue. Rejected as useful but beyond the smallest-safe MVP.

## Risks / Trade-offs

- [Risk] Conservative defaults may miss some useful subagent conclusions. -> Mitigation: keep `defer` as the default for borderline cases and leave richer reviewer workflows to follow-up work.
- [Risk] Envelope metadata may be too sparse for some domains. -> Mitigation: keep the MVP schema small but explicit; add domain-specific enrichments only after real usage proves they are needed.
- [Risk] Different teams may expect subagent findings and verified tool findings to behave the same. -> Mitigation: document the stricter boundary explicitly in specs and context guidance.
- [Risk] Rejected or deferred findings vanish after the turn in MVP-now. -> Mitigation: call this out as an intentional phase boundary and defer durable review queues to later work.

## Migration Plan

1. Update the OpenSpec capability deltas for `netclaw-subagents`, `netclaw-agent-memory`, and `netclaw-session`.
2. Implement findings-capable subagent contract changes and parent-session review logic behind the updated spec.
3. Add actor and memory-pipeline tests for accept, defer, reject, and checkpoint enqueue behavior.
4. Ship with conservative defaults enabled; no migration of existing memory rows is required because this change only narrows future subagent checkpoint admission.
5. Roll back by disabling findings emission for all subagents if the first implementation shows noisy behavior.

## Open Questions

- What exact enum values or scale should MVP use for `durability` and `reusability` so implementation stays simple but testable?
- Should deferred findings be visible in operator diagnostics immediately, or can MVP keep them internal and transient?
- Which first subagents should be findings-capable on day one, if any beyond research-style helpers?

## Deferred Follow-Up

- Operator-visible review surfaces for deferred findings.
- Richer scoring or weighted acceptance models.
- Domain-specific heuristics and adaptive thresholds.
- Persisted deferred-review queues or audit trails for non-accepted findings.
- Broader rollout to more subagent types after the conservative path proves stable.
