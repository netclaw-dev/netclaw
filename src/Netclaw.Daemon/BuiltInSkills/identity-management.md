# Identity Management

<!-- description: How to read and update Netclaw identity files (SOUL.md, AGENTS.md, TOOLING.md) -->

## Overview

Your identity is defined by three files in `~/.netclaw/identity/`. These files
are loaded into every system prompt, so keep them concise and high-signal.

## Identity Files

| File | Purpose | What Belongs Here |
|------|---------|-------------------|
| `SOUL.md` | Who you serve | User's name, family, key relationships, preferences, timezone. Your mental model of the person. |
| `AGENTS.md` | How you operate | Behavioral rules, workflow preferences, operating guidelines. |
| `TOOLING.md` | What you can do | Environment capabilities, installed tools, MCP server notes. |

## How to Edit

1. **Always read first** — use `file_read` to check current content before changing anything.
2. **Use `file_write`** to update the file with the new content.
3. **Be judicious** — only add confirmed facts, not guesses or one-time context.
4. **Keep files small** — aim for quick-scan summaries. Use detail subdirectories for depth.

## Progressive Disclosure

Top-level files should be concise summaries. When a topic needs more depth,
create a detail file in the matching subdirectory:

- `~/.netclaw/identity/soul/` — e.g., `communication-preferences.md`, `work-context.md`
- `~/.netclaw/identity/agents/` — e.g., `tool-policies.md`, `safety-rules.md`
- `~/.netclaw/identity/tooling/` — e.g., `docker.md`, `kubernetes.md`

Reference detail files from the top-level file so they can be loaded on demand.

## Memory Triage — Where to Save What You Learn

When you learn something important, save it to the right place immediately:

| Information Type | Destination | Why |
|-----------------|-------------|-----|
| Personal facts (name, family, relationships, preferences) | `SOUL.md` | Always loaded. Enables you to know what to search for elsewhere. |
| Behavioral rules, workflow preferences | `AGENTS.md` | Always loaded. Guides your operating behavior. |
| Environment capabilities, tool configs | `TOOLING.md` | Always loaded. Tells you what you can do. |
| World knowledge, project details, solutions | **Memorizer** (via `search_memories`) | Cross-session. Organize into workspaces and projects. See memorizer-usage skill. |
| Procedures, reusable workflows | **Skill files** in `~/.netclaw/skills/` | Loaded on demand via `file_read`. |

## SOUL.md Guidelines

SOUL.md should stay small and high-signal — core identity only, not a dump of
everything. It's your mental model of who you serve.

Good entries:
- "Name: Aaron. Lives in Portland, OR. Timezone: America/Los_Angeles."
- "Has a daughter named Clara (age 3) and a dog named Rosie."
- "Prefers concise responses. Dislikes unnecessary caveats."

Bad entries (put these elsewhere):
- Detailed project specifications → Memorizer
- Step-by-step workflows → Skill files
- One-time task context → Let it go after the session
