## Context

Netclaw's skill system relies on `file_read` for loading skill content. This
is a generic file tool that may be restricted by audience policy, provides no
structured metadata, and gives no signal that a "skill load" occurred (vs
reading any other file). For skill creation, the agent uses `file_write` with
no validation of the AgentSkills.io format.

Users have no way to deterministically invoke a skill. The LLM reads the
compressed index and decides whether to load a skill. For scheduled jobs and
reminders, this is unreliable — the job fires, and the LLM may or may not
load the right skill.

**Current components:**
- `SearchToolsTool` — pattern for `[NetclawTool]` tools with `Grant = "builtin"`
- `SkillScanner.ExtractBody()` — already strips frontmatter from skill content
- `SkillScanner.ExtractFrontmatter()` — already parses YAML frontmatter
- `SkillEntry.ResourcePaths` — already enumerates resource files
- `SkillRegistry.Search()` — basic name/description search
- `LlmSessionActor.FireLlmCall()` — turn assembly with context layers
- Claude Code frontmatter spec: `disable-model-invocation`, `invocable`,
  `argument-hint` fields

## Goals / Non-Goals

**Goals:**
- Provide structured, always-available tools for skill loading and management
- Enable deterministic skill invocation via slash commands
- Enforce AgentSkills.io format on skill creation
- Adopt Claude Code's invocation model for consistency with the broader ecosystem

**Non-Goals:**
- Modifying the compressed index format (sibling change)
- Community/external feed support
- Content scanning implementation (stub only, via sibling change)
- Skill dependency resolution

## Decisions

### D1: Three separate tools (not one multi-purpose tool)

**Decision:** `skill_load`, `skill_read_resource`, and `skill_manage` are
three distinct tools, not one tool with a mode parameter.

**Why:** `skill_load` and `skill_read_resource` are read-only and should be
available in all contexts. `skill_manage` is a write tool that may be
restricted in certain audiences. Combining them would either expose writes in
read-only contexts or hide reads behind a write grant.

### D2: skill_manage uses action parameter (like Hermes)

**Decision:** `skill_manage` has a single `Action` parameter that selects the
operation. This follows Hermes's `skill_manage` pattern.

**Why:** Six separate tools (skill_create, skill_edit, etc.) would bloat the
tool registry. A single tool with an action parameter keeps the LLM's tool
list compact while providing full functionality.

**Actions:** `create`, `edit`, `patch`, `delete`, `write_file`, `remove_file`

### D3: Slash commands derived from skill name (Claude Code model)

**Decision:** Every skill's `name` field automatically becomes its slash
command (`/name`). No separate `invoke` or `command` field needed. Two flags
control visibility:
- `disable-model-invocation: true` — LLM cannot auto-trigger (excluded from
  index), but users can type `/name`
- `invocable: false` — Users cannot type `/name`, but LLM can auto-load

**Why:** This matches the Claude Code ecosystem standard. It's simpler than
a separate invocation field and provides fine-grained control over who can
trigger the skill.

### D4: Slash-command dispatch at session actor level

**Decision:** `LlmSessionActor` intercepts messages starting with `/` before
passing to the LLM. If matched, skill content is injected as a transient
system message and the remainder becomes the user message.

**Why not gateway level:** The gateway doesn't have access to the skill
registry. The session actor already has all the context needed.

**Why not as a tool:** Slash commands are user-initiated, not LLM-initiated.
They should resolve before the LLM runs, not as a tool call during the turn.

**Error handling:** Unrecognized `/` commands get a deterministic error
response listing available commands. They are NOT passed to the LLM for
interpretation.

### D5: skill_manage writes to user skills only

**Decision:** `skill_manage` can only write to `~/.netclaw/skills/` (user
area). System skills in `.system/` are read-only. Agent-created skills
automatically get `SkillTrustTier.Agent`.

**Why:** System skills are feed-managed artifacts. Allowing writes would break
hash verification and create divergence from the published feed.

### D6: Atomic writes with temp file pattern

**Decision:** All `skill_manage` write operations use temp file + rename
pattern for crash safety. On validation failure, the temp file is deleted.

**Why:** Follows Hermes's proven pattern. Prevents corrupt skill files from
partial writes during crashes.

### D7: Re-scan after mutation

**Decision:** After any `skill_manage` mutation, the skills directory is
re-scanned and the registry rebuilt (including per-audience menus).

**Why:** Keeps the registry consistent without complex incremental updates.
Re-scanning ~20 SKILL.md files is sub-millisecond.

## Risks / Trade-offs

**[R1] Slash command conflicts with Slack's native `/` commands**
→ Mitigation: Slash-command dispatch happens at the session actor level, after
the Slack adapter has already processed native Slack commands. Only messages
that reach the session are checked.

**[R2] skill_manage allows agent to create arbitrary skills**
→ Mitigation: Frontmatter validation enforces format. `ISkillContentScanner`
stub provides the integration point for future security scanning. Agent
skills get lowest trust tier.

**[R3] Re-scan after every mutation may be slow with many skills**
→ Mitigation: Scanning is file-system enumeration + YAML parsing, not LLM
calls. For <100 skills, this is sub-millisecond. The enrichment service
(trigger phrases) runs asynchronously after rescan.

## Actor Boundaries and Persistence Implications

- All three tools are stateless — they read from `SkillRegistry` (shared
  singleton) and write to disk. No actor messages involved.
- Slash-command dispatch adds logic to `LlmSessionActor` message handling
  but no new persistence events or actor state.
- `skill_manage` mutations trigger `SkillRegistry.Clear()` + re-scan, which
  is the same path used by `SystemSkillSyncService` after feed sync.
