---
name: netclaw-operations
description: "REQUIRED when the user asks about Netclaw capabilities, scheduling, diagnostics, identity updates, or self-maintenance. Read this first — it routes you to the right detail file."
disable-model-invocation: true
metadata:
  author: netclaw
  version: "1.3.0"
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
| Pair remote devices, manage access | [Device Pairing](#device-pairing) |
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

Doctor checks include `exposure-mode`, which validates that the `Daemon`
config section (if present) specifies a supported exposure mode and that
the corresponding tunnel integration is reachable.

Config files: `~/.netclaw/config/netclaw.json` (base config, optional
`Daemon` section for `Host`, `Port`, `ExposureMode`),
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

## Device Pairing

Remote devices authenticate with the daemon using a two-sided pairing protocol.

### Pairing flow

**Daemon side** (requires local/SSH access):

```
shell_execute: netclaw daemon pair
```

This generates a single-use pairing code (8 chars, 5-minute TTL). The code
generation endpoint is loopback-only.

**Client side** (remote device):

```
shell_execute: netclaw pair https://my-daemon.tail1234.ts.net:5000
```

The user is prompted for the pairing code. On success, the bearer token is
saved to `secrets.json` (`DeviceToken` field) and the endpoint is saved to
`netclaw.json`.

### Device management

| Action | Command |
|--------|---------|
| List paired devices | `netclaw daemon devices` |
| Revoke a device | `netclaw daemon devices revoke <name>` |

### Security notes

- Codes: single-use, 8 chars from 32-char alphabet (~1.1 trillion
  combinations), 5-minute TTL
- Rate limiting: 5 attempts/min/IP; after 10 failures, the IP is blocked for
  15 minutes
- When no code is pending, the exchange endpoint returns 404 (invisible to
  scanners)
- Tokens are stored as salted SHA-256 hashes on the daemon; the raw token is
  never persisted server-side

### Config locations

- `~/.netclaw/config/devices.json` — paired device registry (daemon side)
- `~/.netclaw/config/secrets.json` — `DeviceToken` field (client side, added
  by `netclaw pair`)
- `~/.netclaw/config/netclaw.json` — `Daemon` section (`Host`, `Port`,
  `ExposureMode`)
