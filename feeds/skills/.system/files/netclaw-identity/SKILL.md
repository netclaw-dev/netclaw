---
name: netclaw-identity
description: "Netclaw identity files. Read when the user wants to update lasting profile, tone, workflow, or environment preferences that should shape future Netclaw sessions."
metadata:
  author: netclaw
  version: "0.6.0"
  triggers: remember my preference | update how you should respond to me | save this as a standing preference | update your profile for me | keep this in future sessions | change your workflow preference
---

# Netclaw Identity

Use this skill when the user's intent is to change long-lived Netclaw identity
context that should affect future sessions:

- personal preferences or response style
- stable relationship facts and profile details
- lasting workflow rules
- environment/tooling notes that belong in identity, not memory

If the user is asking to remember project facts, research findings, or session
evidence, use `netclaw-memory` instead of editing identity files.

## Identity Files

Your identity is defined by three files in `~/.netclaw/identity/`. These files
are loaded into every system prompt, so keep them concise and high-signal.

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
