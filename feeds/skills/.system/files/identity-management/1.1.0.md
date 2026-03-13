---
name: identity-management
description: How to keep SOUL.md narrowly scoped to identity/profile updates while project facts and evidence stay in SQLite memory
metadata:
  author: netclaw
  version: "1.1.0"
  triggers: learn user preference | update personality | identity profile | save durable fact | soul update
---

## Overview

Your identity is defined by three files in `~/.netclaw/identity/`. These files
are loaded into every system prompt, so keep them concise and high-signal.

## Identity Files

| File | Purpose | What Belongs Here |
|------|---------|-------------------|
| `SOUL.md` | Who you serve | User's name, family, key relationships, stable communication preferences, timezone. |
| `AGENTS.md` | How you operate | Behavioral rules, workflow preferences, operating guidelines. |
| `TOOLING.md` | What you can do | Environment capabilities, installed tools, MCP server notes. |

## SOUL Boundary

`SOUL.md` is a narrow identity/profile surface, not a general memory sink.

Allowed in `SOUL.md`:

- name and relationship facts
- tone / style / voice preferences
- standing communication preferences
- explicit identity/profile updates

Do not put these in `SOUL.md`:

- project facts
- research passages
- tool findings
- troubleshooting evidence
- execution trace or turn-local breadcrumbs

Those belong in SQLite memory via the memory pipeline, not in identity files.

## How to Edit

1. Always read first.
2. Only edit identity files for true identity/profile changes.
3. Keep entries short and durable.
4. Put project and world knowledge in memory, not `SOUL.md`.

## Progressive Disclosure

Top-level files should be concise summaries. When a topic needs more depth,
create a detail file in the matching subdirectory:

- `~/.netclaw/identity/soul/`
- `~/.netclaw/identity/agents/`
- `~/.netclaw/identity/tooling/`

## Memory Triage

| Information Type | Destination |
|-----------------|-------------|
| Personal facts and stable communication preferences | `SOUL.md` |
| Behavioral and workflow rules | `AGENTS.md` |
| Environment capabilities and tool configuration | `TOOLING.md` |
| Project facts, solutions, research, evidence | SQLite memory (`store_memory`, automatic memory, `find_memories`) |

## Rule Of Thumb

If the information should be injected into every prompt forever, it may belong in
an identity file. If it should only be recalled or searched when relevant, it
belongs in memory.
