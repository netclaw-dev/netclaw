## Why

Issue #649 needs deterministic, explicit subagent model selection so operators can target named model/client entries from the multi-model architecture introduced in #648 instead of relying only on role-based routing. We need to add this without breaking existing `modelRole` behavior and with fail-loud startup validation to preserve Netclaw's no-silent-fallback security and operations posture.

## What Changes

- Add optional subagent frontmatter `model: string` and map it to the named model/client registry from #648.
- Define precedence rules: `model` takes priority over `modelRole`; `modelRole` remains the backward-compatible path when `model` is absent.
- Enforce deterministic load/startup failure when `model` is configured but unresolved; do not silently degrade to `modelRole`.
- Extend parser/config/schema/validation and diagnostics surfaces so operator errors are explicit and actionable.
- Add regression tests for precedence, compatibility, and unresolved-model failure semantics.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `netclaw-subagents`: Add explicit subagent-level model selection semantics and precedence between `model` and `modelRole`.
- `netclaw-model-providers`: Add named-model resolution contract for subagent references and fail-loud behavior when references cannot be resolved.

## Impact

### Affected code and systems

- Subagent frontmatter parsing and subagent definition model types.
- Model/client registry resolution path (from #648) used at startup and subagent load time.
- Config schema and doctor/diagnostics output for subagent model configuration errors.
- Subagent spawning and model-selection wiring where `SubAgentDefinition` is resolved.

### APIs and behavior

- Backward compatible for existing subagents that only define `modelRole`.
- **BREAKING (config validation):** subagents that set `model` to an unknown registry name now fail load/startup instead of silently using role-based behavior.

### Security and operational impact

- Preserves no-silent-fallback guarantees by failing closed on unresolved explicit model references.
- Improves operator observability with deterministic startup/load errors and diagnostics for bad model names.
- Avoids hidden model drift where a typo could otherwise route traffic to an unintended default.

### Dependencies and sequencing

- Hard-blocked on issue #648 (named model/client registry architecture).
- Assumes issue #647 frontmatter surface already exists.

### In scope for MVP

- `model` frontmatter support, precedence rules, registry resolution, fail-loud validation, tests, docs/diagnostics updates.

### Out of scope for MVP

- New dynamic fallback policies from explicit `model` to alternate named models.
- Broader redesign of non-subagent model routing behavior beyond defined precedence.

### Source PRDs

- `PRD-001` (MVP runtime determinism and reliability)
- `PRD-002` (fail-closed/default-deny operational posture)
- `PRD-005` (model provider and model selection strategy)
