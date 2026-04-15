## Context

Slash-command dispatch currently treats skill activation as an inline-only operation: inject skill body into the main session as transient system content and continue the turn. Issue #661 requires a second deterministic path where skill authors can declaratively bind a skill to a user-facing subagent via `metadata.subagent`, while preserving strict worker isolation and fail-loud behavior.

This affects four boundaries:

1. Skill metadata parsing and validation (`metadata.subagent` contract).
2. Activation routing consistency across first-party skill activation entry points.
3. Subagent prompt/context composition (overlay semantics and isolation defaults).
4. Routed execution tool authorization behavior.

The change must preserve default-deny behavior and explicitly prohibit silent fallback to inline execution when the routed path is invalid.

## Goals / Non-Goals

**Goals:**

- Make `metadata.subagent` a first-class declarative routing field for first-party skill activation entry points.
- Route deterministically to the named user-facing subagent when metadata is valid.
- Treat skill body as additive subagent system-prompt overlay on routed path.
- Keep routed workers isolated from main-session identity stack and repo-local `AGENTS.md` by default.
- Ensure routed workers inherit the launch audience/boundary context from the parent invocation.
- Fail loudly for unknown, internal-only, or malformed routed targets, with no inline fallback.
- Preserve existing audience-based tool authorization on routed paths for MVP.

**Non-Goals:**

- Introducing implicit fallback behavior when routed activation fails.
- Reworking unrelated subagent lifecycle, timeouts, or tool-loop semantics.
- Global refactor of arbitrary content-retrieval paths (for example raw `file_read`) that are not first-party skill activation entry points.

## Decisions

### D1. Routing precedence is metadata-first across first-party activation entry points

If a skill activation request resolves to a skill with valid `metadata.subagent`, dispatch uses routed subagent execution and does not evaluate inline injection for that activation.

Rationale:

- Ensures deterministic behavior for skill authors and users across entry points.
- Prevents accidental dual-path behavior and hidden policy bypasses.

Alternative considered:

- Attempt routed execution first, then fallback inline on failure. Rejected because it violates fail-loud and default-deny principles.

### D1a. All first-party activation entry points use one shared activation router

Slash-command dispatch, scheduled slash payload dispatch, and any first-party
tool-driven skill activation path SHALL call the same routing resolver.

Rationale:

- Guarantees behavioral parity so one skill does not behave differently depending
  on how it was activated.
- Prevents subtle drift from duplicated routing logic.

Alternative considered:

- Keep per-entrypoint routing logic. Rejected due to high drift risk.

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

### D5. Dispatch-time metadata validation is terminal for routed activation

Malformed `metadata.subagent` values (non-string, empty, invalid name shape)
are treated as deterministic failures when routed activation is selected.
Validation is enforced at dispatch time for every activation request. Scan-time
validation MAY emit warnings, but dispatch-time checks are authoritative.

Rationale:

- Avoids silent misconfiguration and non-obvious behavior drift.
- Makes authoring errors operator-visible immediately.

### D7. Routed tool scope remains audience-governed for MVP

For routed skill executions, tool authorization remains controlled by existing
audience/boundary policy and subagent tool registration. Skill-level
`allowed-tools` is advisory metadata and is not enforced as an additional
runtime gate in this change.

Rationale:

- Preserves current security posture and avoids policy churn in a routing-focused
  change.
- Keeps scope contained; tool-scope intersection rules can be designed as a
  follow-on enhancement.

Alternative considered:

- Enforce intersection of audience policy, subagent tool list, and skill
  `allowed-tools` now. Rejected as a larger policy redesign.

### D8. Routed subagents inherit launch audience context

Routed subagent executions inherit audience/boundary/channel context from the
launching invocation.

Rationale:

- Keeps tool authorization aligned with existing audience policy decisions.
- Prevents accidental escalation by running delegated work under a broader
  audience than the parent turn.

Alternative considered:

- Default delegated runs to personal audience when context is present. Rejected
  because it can bypass tighter audience constraints from the caller.

### D6. Routed failures must include actionable remediation guidance

When routed activation fails, error output must be user-visible and include a
brief remediation hint. At minimum, the message includes target name, failure
reason, and one of: "add the missing subagent definition" or
"fix/remove metadata.subagent on the skill".

Rationale:

- Preserves fail-loud behavior while reducing dead-end failures for operators.
- Makes recovery paths explicit without introducing implicit fallback behavior.

Alternative considered:

- Return terse error codes only. Rejected because it increases operator
  confusion and pushes remediation discovery into docs hunting.

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

- For tool-driven activation paths, should parity be enforced by a shared service
  contract test suite or by per-entrypoint integration tests only?
