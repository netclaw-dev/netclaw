## Context

Slash-command dispatch currently treats skill activation as an inline-only operation: inject skill body into the main session as transient system content and continue the turn. Issue #661 requires a second deterministic path where skill authors can declaratively bind a skill to a user-facing subagent via `metadata.subagent`, while preserving strict worker isolation and fail-loud behavior.

This affects three boundaries:

1. Skill metadata parsing and validation (`metadata.subagent` contract).
2. Slash-command routing precedence (routed subagent path vs inline path).
3. Subagent prompt/context composition (overlay semantics and isolation defaults).

The change must preserve default-deny behavior and explicitly prohibit silent fallback to inline execution when the routed path is invalid.

## Goals / Non-Goals

**Goals:**

- Make `metadata.subagent` a first-class declarative routing field for slash-invoked skills.
- Route deterministically to the named user-facing subagent when metadata is valid.
- Treat skill body as additive subagent system-prompt overlay on routed path.
- Keep routed workers isolated from main-session identity stack and repo-local `AGENTS.md` by default.
- Fail loudly for unknown, internal-only, or malformed routed targets, with no inline fallback.

**Non-Goals:**

- Introducing implicit fallback behavior when routed activation fails.
- Reworking unrelated subagent lifecycle, timeouts, or tool-loop semantics.
- Global refactor of all skill-loading paths beyond slash-command activation and equivalent scheduled payload dispatch.

## Decisions

### D1. Routing precedence is metadata-first for slash-invoked skills

If a matched slash command resolves to a skill with valid `metadata.subagent`, dispatch uses routed subagent execution and does not evaluate inline injection for that activation.

Rationale:

- Ensures deterministic behavior for skill authors and users.
- Prevents accidental dual-path behavior and hidden policy bypasses.

Alternative considered:

- Attempt routed execution first, then fallback inline on failure. Rejected because it violates fail-loud and default-deny principles.

### D2. Routed skill body is a subagent system-prompt overlay

On routed execution, the skill markdown body is appended as an additive specialization layer in the spawned subagent system prompt. It is not emitted as user content and not injected into main-session runtime context for that turn.

Rationale:

- Preserves role semantics (specialization belongs in system layer).
- Avoids contaminating user turn context with configuration instructions.

Alternative considered:

- Keep body in main session and also pass to subagent. Rejected because it duplicates authority and risks inconsistent behavior.

### D3. Routed subagent isolation defaults are explicit and strict

Subagent execution launched by `metadata.subagent` does not inherit the main session identity prompt stack by default and does not auto-load repo-local `AGENTS.md` by default.

Rationale:

- Maintains subagents as isolated workers rather than hidden extensions of the parent prompt stack.
- Reduces privilege drift and hidden coupling to repository-local prompt files.

Alternative considered:

- Inherit parent identity context for convenience. Rejected due to security posture and reproducibility concerns.

### D4. Spawner-level visibility checks gate routed targets

Routing validates target existence and user-facing eligibility before subagent execution. Unknown targets or internal-only targets produce deterministic errors and terminate activation.

Rationale:

- Prevents accidental exposure of internal operational subagents.
- Keeps enforcement near registry/spawner boundary where target metadata is authoritative.

Alternative considered:

- Validate only in slash dispatcher. Rejected as insufficient defense-in-depth.

### D5. Metadata parsing failures are terminal for routed activation

Malformed `metadata.subagent` values (missing, non-string, empty, invalid name shape) are treated as deterministic failures when routed activation is selected. Dispatcher does not reinterpret invalid metadata as "metadata absent".

Rationale:

- Avoids silent misconfiguration and non-obvious behavior drift.
- Makes authoring errors operator-visible immediately.

## Risks / Trade-offs

- [Risk] Existing skills may start failing after adding invalid `metadata.subagent` values. -> Mitigation: add focused validation tests and clear deterministic error text listing accepted format.
- [Risk] Isolation defaults may surprise maintainers expecting inherited identity behavior. -> Mitigation: document in `skill-authoring` system skill and slash-command dispatch docs.
- [Risk] Routing and spawner checks can diverge if duplicated. -> Mitigation: define a shared validation helper used by both dispatcher and spawner boundaries.
- [Risk] Scheduled slash payloads could drift from interactive path. -> Mitigation: reuse the same dispatch branch for interactive and scheduled `/skill ...` messages.

## Migration Plan

1. Land metadata parsing support behind existing skill scan/reload path.
2. Implement dispatcher precedence and error surface for routed activations.
3. Implement/extend spawner eligibility checks for user-facing vs internal-only targets.
4. Wire routed overlay prompt assembly with isolation defaults.
5. Add regression tests, update system skill docs, and validate via OpenSpec tasks.

Rollback:

- Revert dispatcher precedence and metadata parsing changes together to restore inline-only behavior.
- Keep fail-loud behavior consistent during rollback (avoid temporary fallback logic).

## Open Questions

- Should deterministic error messaging include a short remediation hint (for example, "set `metadata.subagent` to a known user-facing subagent") in addition to target name and reason?
- Should `metadata.subagent` validation be enforced at skill scan time only, or both at scan and at dispatch-time (defense-in-depth)?
