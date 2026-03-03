# Skill Authoring

<!-- description: How to create and maintain skill files for reusable procedural knowledge -->

## What Skills Are

Skills are markdown files in `~/.netclaw/skills/` that provide procedural
knowledge — step-by-step instructions, workflows, checklists, and reference
material that you can load on demand via `file_read`.

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

```
~/.netclaw/skills/
  .system/          ← NEVER WRITE HERE (operator-controlled, feed-managed)
  my-skill.md       ← write here
  project-name/     ← subdirectories for organization
    deploy.md
    testing.md
```

Write to `~/.netclaw/skills/` (root or subdirectories). Never write to
`.system/` — those are operator-controlled and overwritten on daemon startup.

## Skill File Format

### Required Elements

1. **`# Heading`** — first `#` heading becomes the display name in the skill index
2. **`<!-- description: ... -->`** — one-line description shown in the always-on
   skill index. Keep it under 200 characters. If omitted, the first paragraph
   is used instead.

### Naming

- Use lowercase letters, numbers, and hyphens only: `deploy-pipeline.md`
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

### Progressive Disclosure (Critical)

**Every file you write should practice progressive disclosure.** This is the
single most important authoring principle in Netclaw. The agent's context
window is finite and expensive — every byte injected has a cost.

**The rule:** Top-level files should be small, scannable summaries. Detailed
content goes in separate files that are loaded on demand via `file_read`.
**No single file should exceed 700 lines.** If it does, split it.

**Apply this everywhere:**

- **Skills** — if a skill grows beyond ~50 lines, split it. Create a short
  summary skill that references detail files. Example:
  ```
  ~/.netclaw/skills/
    k8s-ops.md              ← summary: lists sub-procedures with file paths
    k8s-ops/
      deploy-staging.md     ← detail: full staging deployment steps
      deploy-production.md  ← detail: full production deployment steps
      rollback.md           ← detail: rollback procedure
  ```
- **Identity files** — SOUL.md, AGENTS.md, TOOLING.md should be brief
  summaries. Use `identity/soul/`, `identity/agents/`, `identity/tooling/`
  detail subdirectories for depth.
- **Memories** — use `store_memory` for self-contained documents. Don't try
  to cram everything into one massive memory entry.

**Why this matters:** The skill index shows one-line descriptions for every
skill. If a user has 20 skills, that's 20 lines always in context — cheap.
But if the agent `file_read`s a 500-line skill, that's 500 lines of context
consumed. By splitting into summary + details, the agent only loads what it
needs for the current step.

### Conciseness

The context window is a shared resource. Only add information the agent doesn't
already have. Challenge every paragraph: "Does the agent really need this
explained?" Claude is smart — don't teach it what PDFs are or how libraries work.

- **Be specific and actionable** — concrete steps, not abstract advice
- **Include commands and code blocks** — examples over explanations
- **Note prerequisites** — what must be true before starting
- **Include failure modes** — what to do when something goes wrong
- **Keep skills focused** — one procedure per file. Split complex workflows
  into multiple skills with cross-references.
- **Use consistent terminology** — pick one term and stick with it
- **Use subdirectories** for project-specific skills (the subdirectory name
  becomes the skill's category in the index)

### Reference Structure

Keep references **one level deep** from the main skill file. Avoid chains where
`skill.md` → `advanced.md` → `details.md`. The agent may only partially read
deeply nested files.

Good:
```
skill.md        → references detail-a.md, detail-b.md directly
detail-a.md     → self-contained
detail-b.md     → self-contained
```

For reference files over 100 lines, add a table of contents at the top so the
agent can see the full scope even when previewing.

## Skill Lifecycle

- **Create** when a pattern repeats. Use `file_write` to create the skill file.
- **Update** when the procedure changes. Read first, then write the updated
  version.
- **Delete** when the skill is no longer relevant. But prefer keeping old
  skills — they cost nothing when not loaded.

## How Skills Are Discovered

At daemon startup, all `.md` files under `~/.netclaw/skills/` are scanned
recursively. Each skill appears in the always-on context as a one-liner:

```
skill-name (/home/user/.netclaw/skills/skill-name.md)
  Description from the <!-- description: ... --> comment
```

The full content is **never loaded automatically** — you must `file_read` the
path to get the full instructions. This progressive disclosure keeps the
context window small while making all skills discoverable.
