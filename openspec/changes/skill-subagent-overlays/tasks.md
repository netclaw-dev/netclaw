## 1. Skill metadata and routing contract (Issue #661)

- [ ] 1.1 Add `metadata.subagent` parsing to skill frontmatter model and preserve it on `SkillEntry` metadata.
- [ ] 1.2 Add dispatch-time validation for `metadata.subagent` (type, empty/malformed name) with deterministic error output.
- [ ] 1.3 Optionally add scan-time warnings for malformed routed metadata; dispatch-time checks remain authoritative.
- [ ] 1.4 Add or update `skill-execution-routing` capability implementation scaffolding to encode deterministic precedence and no-fallback behavior.

## 2. Activation routing consistency across entry points

- [ ] 2.1 Introduce a shared activation router used by all first-party activation entry points.
- [ ] 2.2 Update slash-command activation flow to check `metadata.subagent` before inline skill-body injection.
- [ ] 2.3 Update scheduled slash payload handling (reminders/jobs) to use the same router.
- [ ] 2.4 Update tool-driven skill activation entry points to use the same router.
- [ ] 2.5 Preserve existing inline behavior only when `metadata.subagent` is absent.

## 3. Routed subagent prompt assembly and isolation

- [ ] 3.1 Pass skill body to routed subagent as additive system-prompt overlay.
- [ ] 3.2 Ensure routed path does not treat skill body as user runtime context.
- [ ] 3.3 Enforce default isolation: no inherited main-session identity prompt stack unless explicitly configured in a future opt-in.
- [ ] 3.4 Enforce default isolation: no auto-load of repo-local `AGENTS.md` unless explicitly configured in a future opt-in.
- [ ] 3.5 Preserve existing audience-governed tool authorization on routed executions (no new skill-level runtime tool gate in MVP).

## 4. Fail-loud guardrails and deterministic errors

- [ ] 4.1 Return deterministic failure for unknown routed subagent targets.
- [ ] 4.2 Return deterministic failure for internal-only routed subagent targets.
- [ ] 4.3 Return deterministic failure for malformed routed metadata during scan/dispatch.
- [ ] 4.4 Add explicit guard to prevent any silent fallback to inline execution after routed-path failure.
- [ ] 4.5 Ensure routed failures are user-visible and include actionable remediation hints.

## 5. Tests

- [ ] 5.1 Unit-test metadata parsing and validation for `metadata.subagent`.
- [ ] 5.2 Unit-test slash dispatch precedence (routed path chosen when metadata is present and valid).
- [ ] 5.3 Unit-test routed overlay semantics (skill body added to subagent system prompt, not user context).
- [ ] 5.4 Unit-test isolation defaults (no inherited identity stack, no repo `AGENTS.md` auto-load).
- [ ] 5.5 Unit-test failure semantics (unknown target, internal-only target, malformed metadata) and assert no inline fallback.
- [ ] 5.6 Unit-test routed failure messages include target/reason/remediation details.
- [ ] 5.7 Regression-test scheduled slash payload behavior for routed skills.
- [ ] 5.8 Test parity across activation entry points (slash, scheduled slash, and tool-driven activation).
- [ ] 5.9 Test routed executions keep existing audience-governed tool authorization behavior.

## 6. Docs and system-skill updates

- [ ] 6.1 Update `feeds/skills/.system/files/skill-authoring/SKILL.md` with `metadata.subagent` authoring guidance, routed overlay semantics, and no-fallback failure contract.
- [ ] 6.2 Bump `metadata.version` in `feeds/skills/.system/files/skill-authoring/SKILL.md` frontmatter.
- [ ] 6.3 Add or update operator/developer docs for deterministic routed errors and isolation defaults.

## 7. Validation and quality gates

- [ ] 7.1 Run targeted tests for slash command dispatch, skill parsing, and subagent execution changes.
- [ ] 7.2 Run `dotnet slopwatch analyze` and resolve any new violations.
- [ ] 7.3 Run `openspec validate "skill-subagent-overlays"` and confirm artifacts remain implementation-ready.
