## Context

The scanner and registry already map logical names to `SkillEntry.FilePath` and `SkillEntry.SkillDirectory`, and the skill tools already use those mappings. The generated index instead advertises physical roots and direct `file_read`, while refresh callers compose native, server-feed, and external sources differently. Behavioral evals also accept either `skill_load` or `file_read` because both emit a broad `turn_skill_loaded` event.

This change crosses configuration prompt assembly, actor-owned skill tools, daemon sync services, eval instrumentation, and system-skill guidance. It changes no actor messages or persisted data.

## Goals / Non-Goals

**Goals:**

- Present one logical namespace to the model regardless of physical origin.
- Preserve inline and routed activation semantics and Public-audience suppression.
- Make all registry refresh paths use the same live source resolution and precedence.
- Measure the old and new behavior using exact tool-method eval assertions.

**Non-Goals:**

- Projecting a merged filesystem tree.
- Executing or exporting bundled scripts by logical name.
- Changing source precedence, skill frontmatter, persistence, or actor protocols.

## Decisions

### D1. The generated index is origin-free

`SkillRegistry.GenerateIndex` will no longer accept root/source arguments. It will emit names, descriptions, category grouping, slash invocation guidance, and logical tool guidance only.

Alternative: add server-feed roots to the existing header. Rejected because every new source type would continue expanding a model-visible storage routing problem and disclose unnecessary managed paths.

### D2. `skill_load` remains the single activation entry point

The existing tool behavior remains authoritative: inline skills return instructions and resources, while routed skills require `task` and execute the declared subagent. Guidance will describe both behaviors rather than introducing another tool.

Alternative: add separate load and execute tools. Rejected because it broadens the public tool contract without solving the incident.

### D3. A single refresher owns source composition and registry replacement

Add an inventory refresher in the skill subsystem that receives `NetclawPaths`, `SkillFeedsConfig`, configured external sources, the registry, and index layer. Each call resolves currently existing directories for enabled feeds, calls the three-tier `ScanAndMerge`, and applies one result. Startup, sync services, file watching, and `skill_manage` use this seam.

The registry will publish an immutable snapshot built off to the side so readers never observe `Clear` followed by incremental registration. Refresh requests will be serialized to avoid stale writers winning.

Alternative: pass another source list through each existing caller. Rejected because the existing incident and `skill_manage` rescan already demonstrate that duplicated source composition drifts.

### D4. Evals distinguish method from outcome

Normal activation cases require `turn_skill_loaded ... method=skill_load`. Progressive-disclosure cases additionally require `skill_read_resource`. A negative stdout assertion catches attempted `file_read` calls against `SKILL.md`, including wrong paths that cannot emit load telemetry. Explicit physical-inspection coverage remains separate.

## Risks / Trade-offs

- [Risk] Smaller models may initially call `skill_load` without `task` for routed skills. → Mitigation: include routed semantics in the index and retain the deterministic remediation response.
- [Risk] Removing roots affects operator troubleshooting prompts. → Mitigation: keep physical layout in operator CLI documentation, not normal model discovery context.
- [Risk] Refresh centralization changes several constructors. → Mitigation: inject one required refresher and remove parallel source plumbing rather than making security-relevant dependencies optional.
- [Risk] Script-bearing skills may need executable paths. → Mitigation: document execution as unsupported in this change and design a dedicated safe execution/export capability separately.

## Migration Plan

1. Tighten eval assertions and capture an unchanged-code baseline.
2. Add contract tests for logical index output and complete inventory refresh.
3. Switch index generation and refresh callers together.
4. Align embedded guidance, system skills, and documentation.
5. Compare focused and full eval results using the same model settings.

Rollback restores the previous index text and refresh call sites; no stored data or configuration migration is required.

## Open Questions

None. Script execution is intentionally deferred rather than left as an implementation choice.
