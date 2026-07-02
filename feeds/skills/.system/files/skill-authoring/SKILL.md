---
name: skill-authoring
description: "How to create, edit, and manage Netclaw skills. Read this when you need to synthesize a new skill from a session, understand the skill file format, or use the skill_manage tool."
metadata:
  author: netclaw
  version: "1.7.2"
---

# Skill Authoring

This skill documents the complete Netclaw skill format and how to create
skills. Load it when you need to synthesize a skill from a session or help
the user create one.

## Audience and Feature Gating

Skills are subject to two independent gates:

- **Audience gate:** Public sessions cannot load skills (`skill_load`),
  read skill resources (`skill_read_resource`), or see the skill index
  context layer. Skills are fully invisible to Public.
- **Deployment gate:** `SkillSync.Enabled` in `netclaw.json` (default `true`).
  When `false`, skill tools are hidden from discovery for ALL audiences and
  the skill index context layer returns empty.

Both gates must pass for skill features to be available.

## When to Create a Skill

Create a skill when you notice a **repeating pattern** (done 2+ times):
- A multi-step workflow or procedure
- Domain-specific rules that apply across sessions
- Checklists or verification steps for recurring tasks

Do **not** create a skill for:
- One-time facts or observations (use `store_memory` instead)
- Durable user facts or preferences (use `store_memory`, not identity files)
- Tool availability information (already in the tool index)

## Skill File Format

Skills support two layouts:

### Directory layout (recommended)

```
skill-name/
  SKILL.md          # Required: YAML frontmatter + markdown instructions
  references/       # Optional: detail documents loaded on demand
  scripts/          # Optional: executable helpers
  assets/           # Optional: templates, static resources
  tools/            # Optional: any additional files/directories are allowed
```

### Flat-file layout

```
skill-name.md       # YAML frontmatter + markdown instructions (no resources)
```

Flat `.md` files with valid YAML frontmatter are accepted as skills for
compatibility with Claude Code and other platforms. Flat-file skills cannot
have resources. If both `skill-name/SKILL.md` and `skill-name.md` exist, the
directory version takes precedence.

Name matching depends on the source:
- Netclaw-managed local skills use strict identity checks. The frontmatter
  `name` must match the filename (without `.md`) or directory name.
- External sources can use relaxed wrapper names. Netclaw trusts the
  frontmatter `name` as canonical so platforms like Claude Code can expose
  files or directories whose on-disk names do not match the slash command.

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
invocable: false
argument-hint: "[target environment]"
metadata:
  author: your-name
  version: "1.0.0"
  subagent: operations-helper
---
```

| Field | Purpose |
|-------|---------|
| `license` | License identifier or reference |
| `compatibility` | Environment requirements (max 500 chars) |
| `allowed-tools` | Space-delimited tool names this skill needs. Used for audience filtering — if the session lacks these tools, the skill is hidden from the index |
| `disable-model-invocation` | When `true`, the LLM cannot auto-load this skill. Only the user can invoke it via `/name`. Use for side-effect workflows where timing matters (deploys, diagnostics) |
| `invocable` | When `false`, the user cannot invoke via `/name`. Only the LLM auto-loads it. Use for background guidance (reference material, policies) |
| `argument-hint` | Shown after the slash command name for discoverability (e.g., `/deploy [env]`) |
| `metadata.subagent` | Optional declarative route target. When set, first-party activation uses the named user-facing subagent instead of inline skill injection |
| `metadata.version` | Semantic version for cache invalidation and feed tracking |
| `metadata.author` | Author identifier |

### Invocation Model

Every skill's `name` automatically becomes a slash command:
- Skill named `deploy-prod` → user types `/deploy-prod staging`
- The skill content loads as context, `staging` becomes the user's message

If `metadata.subagent` is present and valid:
- slash activation routes to that user-facing subagent
- skill markdown body is appended as a subagent system overlay (not user context)
- the main-session identity stack and repo `AGENTS.md` are not auto-inherited by default
- unknown/internal/malformed routed targets fail loudly with no inline fallback

### `metadata.subagent` consequences and failure modes

Use `metadata.subagent` when you want a skill to always execute through a
specific specialist worker. This is optional; omit it when inline skill
injection is preferred.

Hard consequences when you opt in:
- The skill now depends on a matching user-facing subagent definition being
  present at runtime (from `~/.netclaw/agents/*.md`).
- If the target subagent is missing, `/skill-name` fails with a deterministic
  "not registered" error and the turn is skipped.
- If the target exists but is `internal` visibility, `/skill-name` fails with a
  deterministic "internal-only" error and the turn is skipped.
- If `metadata.subagent` is malformed, activation fails with a deterministic
  metadata error and no inline fallback is attempted.

Authoring guidance:
- Keep `metadata.subagent` aligned with a real, user-facing subagent name.
- If portability matters, document the required subagent in the skill body.
- If you cannot guarantee the subagent will exist, leave `metadata.subagent`
  unset so the skill can run inline.

**Invocation control matrix:**

| Setting | User `/name` | LLM auto-load |
|---------|-------------|----------------|
| (defaults) | Yes | Yes |
| `disable-model-invocation: true` | Yes | No |
| `invocable: false` | No | Yes |

### Markdown Body

After the frontmatter closing `---`, write the skill instructions in markdown.
Keep under 5000 tokens. Include:

- **When to use** — Trigger conditions
- **Procedure** — Step-by-step instructions
- **Pitfalls** — Known failure modes and edge cases
- **Verification** — How to confirm the procedure worked

## Progressive Disclosure

Put detail in additional files, not in the main SKILL.md. Netclaw discovers any
safe relative file under the skill directory as a resource; `references/`,
`scripts/`, and `assets/` are conventions, not hard requirements:

| Directory | Purpose |
|-----------|---------|
| `references/` | Detailed documentation, research, examples |
| `scripts/` | Executable helpers (shell scripts, Python) |
| `assets/` | Templates, static files, config samples |
| `tools/` or other paths | Additional helper files allowed by AgentSkills.io |

The skill body references these files explicitly: "See
`references/deployment-checklist.md` for the full checklist." The agent loads
them on demand via `skill_read_resource`.

## Creating Skills with skill_manage

**Always use `skill_manage` to create or modify skill files.** The
`skill_manage` tool validates frontmatter, scans for prompt injection,
writes atomically, and triggers an immediate registry rescan.

If `file_write` is used instead, the `SkillDirectoryWatcherService` will
detect the change and trigger a rescan automatically (within ~500ms). However,
`skill_manage` is still preferred because it validates content *before*
writing — the watcher only rescans after the fact.

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
- All skills are scanned uniformly regardless of origin — system, user, and all other skills use the same policy (High risk → Reject, Medium → Warn, Low → Allow).
- Rejected scans fail closed: Netclaw returns the rejection reason and leaves the previous on-disk content unchanged.
- Warnings may still allow a mutation or read, but the warning text is surfaced in the tool result.
- Resource paths must be safe relative file paths under the skill directory. Absolute paths, empty segments, `.`, `..`, and root `SKILL.md` are rejected.

Hard rules:
- The frontmatter `name` must match the target skill name for `create` and `edit`.
- If a rescan finds unrelated malformed or unsafe skills, the mutation can still
  succeed, but the tool reports that the rebuilt inventory is degraded.
- Skills with duplicate normalized names, mismatched frontmatter identity,
  symlinked directories/files/resources, or unreadable `SKILL.md` files are
  rejected from the registry until fixed.

## Skill Directories

Skills live in two locations:

| Directory | Source | Editable |
|-----------|--------|----------|
| `~/.netclaw/skills/.system/` | Official Netclaw feed (synced from CDN) | No — read-only |
| `~/.netclaw/skills/.server-feeds/<feed>/` | Private skill-server feeds | No — read-only |
| `~/.netclaw/skills/` (root) | Operator-placed or user-created via `skill_manage` | Yes |

System skills (`.system/`) and private server-feed skills (`.server-feeds/`)
cannot be edited, patched, or deleted via `skill_manage`. They are maintained
by their sync services.

All skills — regardless of origin — are visible in the skill index and
available to all sessions. The skill index is a compressed file listing
injected into the system prompt that points the agent directly at SKILL.md
files on disk for retrieval-led reasoning.
