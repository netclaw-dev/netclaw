## Why

Skills are currently loaded via the generic `file_read` tool, which may be
restricted in Public/Team audiences. There is no structured way to create
skills — the agent uses raw `file_write`. And there is no way for users to
deterministically invoke a skill; the LLM decides whether to load skills,
which fails ~56% of the time.

Slash commands (`/skill-name`) give users a reliable way to trigger skill
loading without depending on the LLM's judgment. Dedicated skill tools
(`skill_load`, `skill_read_resource`, `skill_manage`) provide structured
access that integrates with the trust model and enforces the AgentSkills.io
format.

Ref: PRD-001 FR-006 (Layered System Prompt), PRD-002 (Gateway Security).

## What Changes

- Add `skill_load` tool (`Grant = "builtin"`) — structured skill loading by
  name, returns body + resource manifest, available to all audiences
- Add `skill_read_resource` tool (`Grant = "builtin"`) — scoped resource file
  reading within skill directories, path traversal prevention
- Add `skill_manage` tool (`Grant = "builtin"`) — 6-action CRUD tool for
  skills (create, edit, patch, delete, write_file, remove_file) with
  frontmatter validation and atomic writes
- Add slash-command dispatch — adopt Claude Code invocation model where every
  skill `name` becomes `/name`. Two flags control invocation:
  `disable-model-invocation` and `invocable`
- Extend `SkillFrontmatter` with `disable-model-invocation`, `invocable`,
  and `argument-hint` fields
- Add deterministic error response for unrecognized slash commands

## Capabilities

### New Capabilities

- `skill-tools`: `skill_load`, `skill_read_resource`, and `skill_manage`
  tools for structured skill access and management
- `slash-command-dispatch`: Session-level interception of `/name` messages,
  skill content injection, argument passing, and error handling

### Modified Capabilities

- `netclaw-session`: Session actor intercepts slash commands before LLM
  dispatch, injects matched skill content as transient system message
- `netclaw-tools`: Three new first-party tools registered at startup with
  `Grant = "builtin"`

## Impact

- `src/Netclaw.Actors/Tools/SkillLoadTool.cs` — new tool
- `src/Netclaw.Actors/Tools/SkillReadResourceTool.cs` — new tool
- `src/Netclaw.Actors/Tools/SkillManageTool.cs` — new tool
- `src/Netclaw.Actors/Skills/SkillScanner.cs` — parse new frontmatter fields
- `src/Netclaw.Configuration/SkillEntry.cs` — new properties
- `src/Netclaw.Actors/Skills/SkillRegistry.cs` — slash-command dispatch map
- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs` — slash-command interception
- Depends on `ISkillContentScanner` stub (from sibling change trust-tiers)
