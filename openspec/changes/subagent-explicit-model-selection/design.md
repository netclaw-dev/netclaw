## Context

Issue #649 extends subagent frontmatter model selection on top of two existing/planned contracts: #647 introduces the subagent frontmatter surface and #648 introduces a named model/client registry for multi-model routing. Today subagents rely on `modelRole`, which is not sufficient when operators need deterministic, per-subagent targeting to a specific named model/client profile.

This is a cross-cutting change across parsing, model resolution, startup validation, and diagnostics:

1. Parse optional `model` from subagent markdown frontmatter and carry it on the in-memory definition.
2. Resolve `model` through the #648 named registry.
3. Apply deterministic precedence (`model` over `modelRole`) while retaining backward compatibility.
4. Fail loudly at load/startup when explicit `model` values are unresolved, with no silent fallback.

Actor boundary note: subagent runtime remains an ephemeral actor (`SubAgentActor`) and this change only alters model selection inputs before/at spawn. Persistence implications are limited to validation/diagnostic state and not session journal schema.

## Goals / Non-Goals

**Goals:**

- Add optional frontmatter `model: string` for subagents.
- Resolve `model` against named model/client registry entries from #648.
- Preserve existing `modelRole` behavior when `model` is absent.
- Enforce precedence: if both are set, `model` wins.
- Enforce fail-loud startup/load semantics for unresolved explicit `model` values.
- Provide actionable diagnostics/doctor output for unresolved names.

**Non-Goals:**

- Implementing #648 registry architecture itself (this change consumes it).
- Replacing or removing `modelRole` in MVP.
- Adding runtime auto-fallback from explicit `model` to alternate named models.
- Redesigning provider failover semantics beyond existing provider/model behavior.

## Decisions

### D1. `model` is an optional frontmatter field on subagent definitions

Subagent frontmatter gains `model` as an optional string and maps to a corresponding optional property on `SubAgentDefinition`.

Rationale:

- Keeps configuration local to the subagent definition.
- Aligns with explicit, declarative routing style already used in frontmatter-driven contracts.

Alternative considered:

- Add explicit model only in central config, not subagent frontmatter. Rejected because it weakens portability and makes subagent definitions less self-describing.

### D2. Selection precedence is deterministic: `model` > `modelRole`

Selection order is fixed:

1. If `model` is present, resolve and use explicit named model/client entry.
2. Else use existing `modelRole` logic (including current defaults/fallbacks already defined by existing requirements).

Rationale:

- Avoids ambiguity when both fields are present.
- Preserves backward compatibility for existing subagents without explicit `model`.

Alternative considered:

- Reject definitions that specify both fields. Rejected because it breaks compatibility and removes a safe migration path.

### D3. Unresolved explicit `model` is a startup/load failure

If `model` cannot be resolved to a named registry entry, subagent load/startup fails deterministically. The system does not silently retry with `modelRole`.

Rationale:

- Matches Netclaw fail-closed/no-silent-fallback posture.
- Prevents typo-driven drift to unintended models.

Alternative considered:

- Warn and fall back to `modelRole`. Rejected because it hides misconfiguration and violates explicit model intent.

### D4. Validation occurs at load boundary with diagnostics surfaced to operators

Validation runs when subagent definitions are loaded/validated at startup (or equivalent reload boundary), so invalid explicit model references fail before runtime task execution. Diagnostics include unresolved model name and owning subagent identity.

Rationale:

- Moves failure earlier and makes operations predictable.
- Avoids latent runtime failures deep in actor execution paths.

Alternative considered:

- Defer resolution until subagent spawn. Rejected as too late and harder to operate safely.

## Risks / Trade-offs

- [Risk] #648 naming conventions may drift from subagent author expectations. -> Mitigation: validate against exact registry keys and provide deterministic error messaging with known-name hints when available.
- [Risk] Existing subagents with newly-added but misspelled `model` now block startup/load. -> Mitigation: clear doctor/diagnostic output and migration guidance in docs.
- [Risk] Dual-field (`model` + `modelRole`) definitions may confuse maintainers about active path. -> Mitigation: codify precedence in spec/tests/docs and include diagnostics that report effective selection source.
- [Risk] Validation logic duplicated across parser and runtime could diverge. -> Mitigation: centralize resolution/validation helper and reuse from load pipeline.

## Migration Plan

1. Add `model` field parsing and in-memory representation.
2. Wire explicit-model resolution against #648 registry abstraction.
3. Implement precedence logic and fail-loud unresolved behavior.
4. Add diagnostics/doctor output for unresolved model references.
5. Add tests for compatibility, precedence, and no-fallback failure behavior.
6. Update operator and subagent authoring docs.

Rollback:

- Revert explicit `model` parsing/resolution and precedence wiring together.
- Keep existing `modelRole` path unchanged.
- If rollback is required after deployment, remove `model` entries from subagent frontmatter to restore pre-change behavior.

## Open Questions

- Should diagnostics print all valid registry names or only the closest matches to reduce noise?
- On hot-reload paths, should unresolved `model` reject only the changed subagent set or fail the entire reload transaction?
