---
name: skill-authoring
description: Create a skill when a repeatable workflow emerges. Read for file format, naming, and discovery conventions.
metadata:
  author: netclaw
  version: "0.6.0"
  triggers: repeating workflow | create skill file | skill format question | update skill
---

## What Skills Are

Skills are procedural knowledge — step-by-step instructions, workflows,
checklists, and reference material that the agent can load on demand via
`file_read`.

Skills differ from memories: memories are facts you retrieve via search, skills
are procedures you follow when doing a specific type of work.

## When to Create a Skill

Create a skill when you notice a **repeatable pattern** — something you've done
more than once that has specific steps, decisions, or context worth capturing:

- Deployment workflows for specific projects
- Troubleshooting runbooks for recurring issues
- Coding conventions for a particular codebase
- Integration patterns (API usage, data formats)
- Operational procedures (backup, restore, monitoring)

**Don't create a skill for:**
- One-time facts → use `store_memory` instead
- Personal preferences → put in `AGENTS.md` or `SOUL.md`
- Tool availability → put in `TOOLING.md`

## Where to Write

Skills live in `~/.netclaw/skills/`. Two layouts are supported:

```
~/.netclaw/skills/
  .system/              ← NEVER WRITE HERE (operator-controlled, feed-managed)
  my-skill.md           ← flat file skill (simple)
  k8s-ops/              ← directory-based skill (for progressive disclosure)
    SKILL.md            ← main skill file
    references/         ← detail files loaded on demand
      deploy-staging.md
      deploy-prod.md
    scripts/            ← executable helpers
      health-check.sh
```

Write to `~/.netclaw/skills/` (root or subdirectories). Never write to
`.system/` — those are operator-controlled and overwritten on daemon startup.

## Skill File Format (AgentSkills.io Standard)

Skills use YAML frontmatter followed by markdown content.

### Required Frontmatter

```yaml
---
name: deploy-pipeline
description: Deploy staging and production environments with smoke tests. Use when deploying services or handling rollbacks.
---
```

- **`name`** — lowercase letters, numbers, and hyphens only. Must match the
  filename (for flat files) or directory name (for `SKILL.md`).
- **`description`** — what the skill does AND when to use it. Max 1024 chars.
  This appears in the always-on skill index every turn.

### Optional Frontmatter

```yaml
---
name: deploy-pipeline
description: Deploy staging and production environments with smoke tests.
license: MIT
compatibility: Requires kubectl and helm
allowed-tools: Bash(kubectl:*) Bash(helm:*) Read
metadata:
  author: my-team
  version: "0.6.0"
  triggers: deploy to staging | deploy to production | rollback needed
---
```

- **`metadata.triggers`** — pipe-separated activation conditions. Shown as
  `LOAD WHEN:` in the skill index. Focus on observable situations (2-5 words
  each). Optional — the description alone is sufficient for most skills.
- **`license`** — license name or reference.
- **`compatibility`** — environment requirements.
- **`allowed-tools`** — pre-approved tools (experimental).
- **`metadata.version`** — skill version for tracking changes.

### Naming

- Use lowercase letters, numbers, and hyphens only: `deploy-pipeline`
- Prefer gerund form: `processing-logs`, `managing-backups`, `testing-api`
- Action-oriented is also fine: `deploy-staging`, `rotate-certs`
- Avoid vague names: `helper`, `utils`, `misc`

### Descriptions

Write in **third person**. The description is injected into the system prompt —
inconsistent point-of-view confuses skill discovery.

- Good: `Deploys staging environment and runs smoke tests`
- Bad: `I can help you deploy to staging`
- Bad: `Use this to deploy to staging`

Include both **what** the skill does and **when** to use it.

## Directory-Based Skills (Progressive Disclosure)

For skills with detailed sub-procedures, use the directory layout:

```
k8s-ops/
  SKILL.md              ← Tier 2: summary loaded on activation
  references/           ← Tier 3: loaded only when needed
    deploy-staging.md
    deploy-production.md
    rollback.md
  scripts/
    health-check.sh
```

The `SKILL.md` body should reference sub-resources by relative path:

```markdown
## Deployment Procedures

- Staging: see [references/deploy-staging.md](references/deploy-staging.md)
- Production: see [references/deploy-production.md](references/deploy-production.md)
- Rollback: see [references/rollback.md](references/rollback.md)
```

The agent loads only the reference it needs for the current task.

### Progressive Disclosure (Critical)

**Every file you write should practice progressive disclosure.** The agent's
context window is finite and expensive — every byte injected has a cost.

**The rule:** Top-level files should be small, scannable summaries. Detailed
content goes in separate files loaded on demand via `file_read`.
**No single file should exceed 700 lines.** If it does, split it.

Standard subdirectories recognized by the scanner:
- `scripts/` — executable code
- `references/` — detailed documentation
- `assets/` — templates, data files, schemas

### Reference Structure

Keep references **one level deep** from the main skill file. Avoid chains where
`SKILL.md` → `advanced.md` → `details.md`. The agent may only partially read
deeply nested files.

## Conciseness

The context window is a shared resource. Only add information the agent doesn't
already have.

- **Be specific and actionable** — concrete steps, not abstract advice
- **Include commands and code blocks** — examples over explanations
- **Note prerequisites** — what must be true before starting
- **Include failure modes** — what to do when something goes wrong
- **Keep skills focused** — one procedure per file
- **Use consistent terminology** — pick one term and stick with it

## Skill Lifecycle

- **Create** when a pattern repeats. Use `file_write` to create the skill file.
- **Update** when the procedure changes. Read first, then write the updated
  version.
- **Delete** when the skill is no longer relevant. But prefer keeping old
  skills — they cost nothing when not loaded.

## How Skills Are Discovered

At daemon startup, all skills under `~/.netclaw/skills/` are scanned.
Directory-based skills (`name/SKILL.md`) take precedence over flat files
(`name.md`). Each skill appears in the always-on context as a one-liner:

```
skill-name (/home/user/.netclaw/skills/skill-name/SKILL.md)
  Description from YAML frontmatter
  LOAD WHEN: condition1 | condition2 | condition3
  [3 resources in /home/user/.netclaw/skills/skill-name]
```

The full content is **never loaded automatically** — you must `file_read` the
path to get the full instructions. This progressive disclosure keeps the
context window small while making all skills discoverable.
