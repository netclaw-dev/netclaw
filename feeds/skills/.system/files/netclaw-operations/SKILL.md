---
name: netclaw-operations
description: "REQUIRED when the user asks about Netclaw capabilities, scheduling, diagnostics, identity updates, or self-maintenance. Read this first — it routes you to the right detail file."
disable-model-invocation: true
metadata:
  author: netclaw
  version: "1.2.0"
---

# Netclaw Operations

This is your operational guide. Load it when the user's request is about
Netclaw itself — what it can do, how to schedule work, how to diagnose
problems, how to update preferences, or how to maintain itself.

## Route by Intent

| User intent | Section below |
|-------------|---------------|
| Schedule reminders, cron jobs | [Scheduling](#scheduling) |
| Discover MCP tools | [Tool Discovery](#tool-discovery) |
| Something is broken, debug it | [Diagnostics](#diagnostics) |
| Update preferences, tone, profile | [Identity](#identity) |
| Check health, update self | [Self-Maintenance](#self-maintenance) |

## Scheduling

`set_reminder` accepts three schedule types:

| Type | Examples |
|------|---------|
| `once` | `"30m"`, `"2h"`, `"2026-03-15T14:30:00Z"` |
| `interval` | `"30m"`, `"6h"`, `"1d"` |
| `cron` | `"0 */6 * * *"`, `"0 9 * * MON-FRI"` |

Parameters: `name` (human-readable), `prompt` (what to execute),
`schedule_type`, `schedule`, `report_to_channel` (optional Slack channel),
`notify_instructions` (optional formatting).

Other scheduling tools: `list_reminders`, `cancel_reminder`,
`get_reminder_history`.

## Tool Discovery

MCP tools are not loaded by default. Use `search_tools` to discover them:

```
search_tools(query: "servers")                  # list all MCP servers
search_tools(query: "all", server: "notion")    # browse a server's tools
search_tools(query: "email")                    # keyword search
```

After discovery, matched tools become callable for the session.

Sessions receive granted tool categories. `builtin` is always granted.
Other categories (`web`, `file`, `shell`, `scheduling`) depend on ACL
config. If a tool is missing, it may not be granted for this session.

## Diagnostics

When something seems wrong with Netclaw itself:

1. Run `netclaw doctor` via `shell_execute` — validates config, providers,
   MCP connections, memory health
2. If doctor reports fixable issues, run `netclaw doctor --fix --dry-run` to
   preview auto-repairs (schema-driven: stale properties, enum coercion, missing defaults)
3. Run `netclaw status` via `shell_execute` — live runtime state from daemon
3. Check daemon logs at `~/.netclaw/logs/daemon-{yyyy-MM-dd}.log`
4. Check session logs at `~/.netclaw/sessions/{session-id}/logs/`

| Symptom | Check |
|---------|-------|
| No LLM responses | `netclaw doctor`; verify provider credentials |
| Missing tools | `netclaw mcp list`; check MCP connection state |
| Memory recall degraded | `netclaw status` memory section |
| Daemon won't start | crash logs at `~/.netclaw/logs/crash-*.log` |

Config files: `~/.netclaw/config/netclaw.json` (base config),
`~/.netclaw/config/secrets.json` (credentials — never display API keys).

## Identity

Your identity is defined by three files loaded into every session prompt:

| File | Purpose |
|------|---------|
| `~/.netclaw/identity/SOUL.md` | Who you serve — name, relationships, preferences, timezone |
| `~/.netclaw/identity/AGENTS.md` | How you operate — behavioral rules, workflow preferences |
| `~/.netclaw/identity/TOOLING.md` | What you can do — environment, tools, MCP notes |

To edit: read the file first with `file_read`, then write with `file_write`.
Keep entries concise and durable. Detail subdirectories exist for depth:
`identity/soul/`, `identity/agents/`, `identity/tooling/`.

**Identity vs memory:** If it should shape every future session → identity
file. If it should be recalled when relevant → SQLite memory.

## Self-Maintenance

| Action | Command (via `shell_execute`) |
|--------|-------------------------------|
| Check for updates | `netclaw update` |
| Self-diagnose | `netclaw doctor` |
| Runtime health | `netclaw status` |
| Memory/token stats | `netclaw stats` |
| List past sessions | `netclaw sessions --once` |
| Inspect reminder history | `netclaw reminder history <id> --last 5` |
