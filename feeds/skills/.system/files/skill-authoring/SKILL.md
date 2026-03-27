---
name: skill-authoring
description: "How to create, edit, and manage Netclaw skills. Read this when you need to synthesize a new skill from a session, understand the skill file format, or use the skill_manage tool."
metadata:
  author: netclaw
  version: "1.2.0"
---

# Skill Authoring

This skill documents the complete Netclaw skill format and how to create
skills. Load it when you need to synthesize a skill from a session or help
the user create one.

## When to Create a Skill

Create a skill when you notice a **repeating pattern** (done 2+ times):
- A multi-step workflow or procedure
- Domain-specific rules that apply across sessions
- Checklists or verification steps for recurring tasks

Do **not** create a skill for:
- One-time facts or observations (use `store_memory` instead)
- Personal preferences or profile data (use identity files: SOUL.md, AGENTS.md)
- Tool availability information (already in the tool index)

## Skill File Format

Skills follow the [AgentSkills.io](https://agentskills.io) directory layout:

```
skill-name/
  SKILL.md          # Required: YAML frontmatter + markdown instructions
  references/       # Optional: detail documents loaded on demand
  scripts/          # Optional: executable helpers
  assets/           # Optional: templates, static resources
```

### YAML Frontmatter (Required)

```yaml
---
name: my-skill-name
description: "1-2 sentences: what this skill does AND when to use it."
---
```

**Required fields:**
- `name` — Lowercase letters, numbers, hyphens. Max 64 chars. This also
  becomes the slash command (`/my-skill-name`).
- `description` — Max 1024 chars. Must describe both what it does and when
  to use it, since this appears in the compressed skill index.

### Optional Frontmatter Fields

```yaml
---
name: my-skill-name
description: "What it does and when to use it."
license: MIT
compatibility: "Requires Python 3.10+"
allowed-tools: shell_execute web_search
disable-model-invocation: true
user-invocable: false
argument-hint: "[target environment]"
metadata:
  author: your-name
  version: "1.0.0"
---
```

| Field | Purpose |
|-------|---------|
| `license` | License identifier or reference |
| `compatibility` | Environment requirements (max 500 chars) |
| `allowed-tools` | Space-delimited tool names this skill needs. Used for audience filtering — if the session lacks these tools, the skill is hidden from the index |
| `disable-model-invocation` | When `true`, the LLM cannot auto-load this skill. Only the user can invoke it via `/name`. Use for side-effect workflows where timing matters (deploys, diagnostics) |
| `user-invocable` | When `false`, the user cannot invoke via `/name`. Only the LLM auto-loads it. Use for background guidance (reference material, policies) |
| `argument-hint` | Shown after the slash command name for discoverability (e.g., `/deploy [env]`) |
| `metadata.version` | Semantic version for cache invalidation and feed tracking |
| `metadata.author` | Author identifier |

### Invocation Model

Every skill's `name` automatically becomes a slash command:
- Skill named `deploy-prod` → user types `/deploy-prod staging`
- The skill content loads as context, `staging` becomes the user's message

**Invocation control matrix:**

| Setting | User `/name` | LLM auto-load |
|---------|-------------|----------------|
| (defaults) | Yes | Yes |
| `disable-model-invocation: true` | Yes | No |
| `user-invocable: false` | No | Yes |

### Markdown Body

After the frontmatter closing `---`, write the skill instructions in markdown.
Keep under 5000 tokens. Include:

- **When to use** — Trigger conditions
- **Procedure** — Step-by-step instructions
- **Pitfalls** — Known failure modes and edge cases
- **Verification** — How to confirm the procedure worked

## Progressive Disclosure

Put detail in subdirectories, not in the main SKILL.md:

| Directory | Purpose |
|-----------|---------|
| `references/` | Detailed documentation, research, examples |
| `scripts/` | Executable helpers (shell scripts, Python) |
| `assets/` | Templates, static files, config samples |

The skill body references these files explicitly: "See
`references/deployment-checklist.md` for the full checklist." The agent loads
them on demand via `skill_read_resource`.

## Creating Skills with skill_manage

**IMPORTANT: NEVER use `file_write` to create or modify skill files.** The
`file_write` tool writes to disk but does NOT register the skill in the
in-memory `SkillRegistry`. The skill will exist on disk but be invisible to
`skill_load`, the skill index, and `netclaw stats` until the next daemon
restart. Always use `skill_manage` — it validates frontmatter, writes
atomically, and triggers an immediate registry rescan.

Use the `skill_manage` tool for all skill mutations:

```
skill_manage(action: "create", name: "my-workflow", content: "---\nname: ...\n---\n# ...")
skill_manage(action: "edit", name: "my-workflow", content: "...")
skill_manage(action: "patch", name: "my-workflow", oldString: "old", newString: "new")
skill_manage(action: "delete", name: "my-workflow")
skill_manage(action: "write_file", name: "my-workflow", filePath: "references/guide.md", fileContent: "...")
skill_manage(action: "remove_file", name: "my-workflow", filePath: "references/old.md")
```

The tool validates frontmatter, enforces the AgentSkills.io format, writes
atomically, and triggers a registry rescan after mutations.

Content scanning rules:
- `SKILL.md` and prompt-facing resource files are scanned for prompt-injection patterns before Netclaw persists or loads them.
- `skill_manage` mutations are scanned with at least Community-tier policy even though the created skill lives in the User tier, so model-authored edits do not bypass the guardrail.
- Rejected scans fail closed: Netclaw returns the rejection reason and leaves the previous on-disk content unchanged.
- Warnings may still allow a mutation or read, but the warning text is surfaced in the tool result.
- Resource files must stay under `references/`, `scripts/`, or `assets/`; other paths are rejected.

Hard rules:
- The frontmatter `name` must match the target skill name for `create` and `edit`.
- If a rescan finds unrelated malformed or unsafe skills, the mutation can still
  succeed, but the tool reports that the rebuilt inventory is degraded.
- Skills with duplicate normalized names, mismatched frontmatter identity,
  symlinked directories/files/resources, or unreadable `SKILL.md` files are
  rejected from the registry until fixed.

## Trust Tiers

Skills are classified by trust tier based on their directory location:

| Tier | Directory | Source | Default Min Audience |
|------|-----------|--------|---------------------|
| System | `.system/` | Official Netclaw feed | Team |
| User | root `~/.netclaw/skills/` | Operator-placed or user-created | Team |
| Community | `.community/` | Netclaw org community feed | Team |
| External | `.external/` | Third-party marketplaces | Personal |
| Agent | `.agent/` | Agent auto-synthesized | Personal |

By default, no skills are visible to Public sessions. To make a skill visible
in Public, add `minimum-audience: public` to the frontmatter — only System
and User tier skills may do this.

Skills created via `skill_manage` get the User tier. The Agent tier is
reserved for future auto-authoring where the agent creates skills without
user direction. The tier is determined by directory location — a skill cannot
self-declare a higher tier.
