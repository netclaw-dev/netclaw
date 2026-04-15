## 1. Dependency gate and planning alignment (#649 depends on #648)

- [ ] 1.1 Confirm issue #648 named model/client registry contracts are merged and available to subagent resolution paths.
- [ ] 1.2 Confirm issue #647 subagent frontmatter parsing surface is present in current branch baseline.
- [ ] 1.3 Document dependency status in implementation notes and keep #649 blocked until #648 prerequisites are satisfied.

## 2. Frontmatter and definition model updates

- [ ] 2.1 Add optional subagent frontmatter field `model: string` to parser/input contracts used for subagent markdown.
- [ ] 2.2 Extend `SubAgentDefinition` (or equivalent runtime definition type) with optional explicit model property mapped from frontmatter `model`.
- [ ] 2.3 Preserve existing `modelRole` parsing behavior unchanged for backward compatibility.

## 3. Selection precedence and runtime wiring

- [ ] 3.1 Implement deterministic precedence logic: explicit `model` resolution path executes before `modelRole` path.
- [ ] 3.2 Implement compatibility branch: when `model` is absent, keep current `modelRole` behavior (including existing defaults/fallbacks).
- [ ] 3.3 Implement dual-field behavior: when both `model` and `modelRole` exist, `model` wins and `modelRole` is ignored for selection.

## 4. Validation, schema, and diagnostics (fail-loud)

- [ ] 4.1 Add startup/load validation that resolves every explicit subagent `model` against the #648 named model/client registry.
- [ ] 4.2 Enforce fail-loud behavior for unresolved explicit `model` references with deterministic errors; do not silently fall back to `modelRole`.
- [ ] 4.3 Update any relevant configuration/schema contracts impacted by new subagent `model` field and validation surface.
- [ ] 4.4 Update diagnostics/doctor output to report unresolved explicit model names with owning subagent identifiers.

## 5. Tests for precedence, compatibility, and failures

- [ ] 5.1 Add parser tests for optional `model` frontmatter (present, absent, and combined with `modelRole`).
- [ ] 5.2 Add model-selection tests proving precedence (`model` present -> explicit path, both present -> `model` wins).
- [ ] 5.3 Add backward-compatibility tests proving `modelRole` behavior remains when `model` is absent.
- [ ] 5.4 Add failure tests proving unresolved explicit `model` fails startup/load and never silently falls back.
- [ ] 5.5 Add diagnostics/doctor tests proving unresolved explicit model errors are actionable.

## 6. Documentation and OpenSpec traceability

- [ ] 6.1 Update operator/developer docs for subagent frontmatter to include `model`, precedence rules, and no-silent-fallback semantics.
- [ ] 6.2 Reference issue #649 and dependency #648 in the implementation PR description and change notes.
- [ ] 6.3 Keep OpenSpec artifacts aligned if implementation details require additional requirement deltas.

## 7. Verification and quality gates

- [ ] 7.1 Run targeted test suites covering subagent parsing, model resolution, and startup validation paths.
- [ ] 7.2 Run `dotnet slopwatch analyze` and resolve any newly introduced violations.
- [ ] 7.3 Run `openspec validate "subagent-explicit-model-selection"` to confirm implementation readiness and artifact consistency.
