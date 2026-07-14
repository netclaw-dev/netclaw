## Why

Server-feed skills can be discovered correctly while the model is still told to infer their physical location from an incomplete root list. This lets a valid skill appear in the index yet fail at use time when the model reads the wrong `SKILL.md` or resource path; PRD-001 requires deterministic runtime behavior and PRD-002 requires avoiding unnecessary disclosure of managed filesystem structure.

## What Changes

- Make logical skill names the normal model-facing access contract: `skill_load` for activation and `skill_read_resource` for progressive-disclosure resources.
- Remove physical skill roots and direct `file_read` guidance from the generated skill index and embedded operating guidance.
- Distinguish inline instruction loading from `metadata.subagent` routed execution in model guidance.
- Make every registry refresh scan the same live native, server-feed, and external source set with existing precedence.
- Tighten behavioral evals so direct `file_read` of `SKILL.md` does not count as normal skill activation.
- Preserve explicit operator/user inspection of physical files as an exceptional filesystem workflow.

### In scope for MVP

- Logical skill discovery, activation, and resource reads.
- Consistent inventory refresh after startup, sync, file watching, and `skill_manage` mutations.
- Prompt, system-skill, documentation, unit/integration test, and eval alignment.

### Out of scope for MVP

- Filesystem overlays, symlink projections, or copied resolved skill trees.
- A general skill script execution or export API.
- Changes to native > server-feed > external precedence.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `skill-index-compression`: Replace physical-root/direct-file retrieval guidance with a logical tool-mediated index.
- `skill-tools`: Define logical access behavior and require full-source inventory preservation after refreshes.

## Impact

- Affected runtime code: skill index generation, registry refresh coordination, skill management rescans, and embedded operating guidance.
- Affected validation: skill registry/tool/sync tests and behavioral eval assertions.
- Affected operational guidance: `skill-authoring`, Netclaw operations references, and repository agent guidance.
- Public sessions remain unable to see the skill index or use hidden skill access tools; managed origins remain read-only.
- No configuration schema, persistence schema, actor message, or public CLI wire-format change is introduced.
