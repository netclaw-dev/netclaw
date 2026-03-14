---
name: capability-reference
description: "Quick-lookup catalog of all built-in tools, CLI commands, tool grant categories, scheduling syntax, and MCP discovery. Read when unsure what tools or commands are available."
metadata:
  author: netclaw
  version: "1.1.0"
  triggers: what can I do | available tools | CLI commands | how to schedule | tool grants | MCP discovery | list capabilities
---

## Session Context

Each turn injects the current session ID and channel info into the system
prompt. Use the session ID to:

- reference yourself in scheduled reminders
- correlate with `netclaw sessions` output for diagnostics
- identify which session is running during troubleshooting

## Built-in Tools by Grant Category

### builtin (always granted)

| Tool | Purpose |
|------|---------|
| `search_tools` | Discover and load MCP tools by keyword or server |
| `store_memory` | Save knowledge to cross-session memory |
| `find_memories` | Search memory (returns IDs, titles, snippets) |
| `get_memories` | Load full memory content by ID |
| `update_memory` | Edit or delete a memory by ID |
| `lookup_slack_user` | Find Slack user by name/email, returns user ID |
| `send_slack_message` | Send message to Slack channel or DM |

### web

| Tool | Purpose |
|------|---------|
| `web_search` | Search the web, returns titles/URLs/snippets |
| `web_fetch` | Fetch URL, save text to local file, return preview |

### file

| Tool | Purpose |
|------|---------|
| `file_read` | Read file contents as text |
| `file_write` | Write content to file, creates parent dirs |
| `attach_file` | Attach a file to send to the user |

### shell

| Tool | Purpose |
|------|---------|
| `shell_execute` | Run shell command with timeout, returns stdout/stderr |

### scheduling

| Tool | Purpose |
|------|---------|
| `set_reminder` | Schedule one-shot, interval, or cron reminders |
| `list_reminders` | List reminders with IDs, status, next fire times |
| `cancel_reminder` | Delete a reminder by ID |

## Tool Grant System

Sessions receive a set of granted tool categories. Tools outside the grant set
are unavailable for that session. `builtin` is always granted. Other categories
(`web`, `file`, `shell`, `scheduling`) are granted per ACL configuration in
`netclaw.json`.

## MCP Discovery (search_tools)

MCP tools are not loaded into the prompt by default. Use `search_tools` to
discover and load them:

```
search_tools(query: "servers")                     # list all MCP servers
search_tools(query: "all", server: "memorizer")    # browse all tools in one server
search_tools(query: "email")                       # keyword search across all servers
```

After a search returns matching tools, those tools become callable. If no exact
match is found, `search_tools` suggests similar tools.

Generated MCP catalogs are cached at `identity/tooling/shadow/mcp/<server>.md`.

## Scheduling Quick Reference

`set_reminder` accepts three schedule types:

| Type | Schedule value examples |
|------|----------------------|
| `once` | `"30m"`, `"2h"`, `"2026-03-15T14:30:00Z"` |
| `interval` | `"30m"`, `"6h"`, `"1d"` |
| `cron` | `"0 */6 * * *"`, `"0 9 * * MON-FRI"` |

Key parameters: `name` (human-readable ID), `prompt` (instructions to execute),
`schedule_type`, `schedule`, `report_to_channel` (optional Slack channel for
results), `notify_instructions` (optional output formatting guidance).

The agent can reference its own session ID when creating reminders to tie
scheduled work back to the originating conversation.

## CLI Commands

### Interaction

| Command | Purpose |
|---------|---------|
| `netclaw chat` | Interactive TUI chat session |
| `netclaw chat --resume <id>` | Resume existing session by ID |
| `netclaw sessions` | Browse and resume recent sessions (TUI) |
| `netclaw sessions --once` | List sessions and exit (plain text or `--json`) |
| `netclaw -p "prompt"` | Headless single-prompt mode |

### Daemon

| Command | Purpose |
|---------|---------|
| `netclaw daemon start` | Start daemon as background process |
| `netclaw daemon stop` | Stop daemon gracefully |
| `netclaw daemon status` | Show daemon process status |
| `netclaw daemon install` | Install systemd user service (Linux) |
| `netclaw daemon uninstall` | Remove systemd user service |

### Diagnostics

| Command | Purpose |
|---------|---------|
| `netclaw doctor` | Offline config validation (see `self-diagnostics`) |
| `netclaw status` | Runtime health from daemon endpoint |

### Configuration

| Command | Purpose |
|---------|---------|
| `netclaw init` | First-run setup wizard |
| `netclaw provider` | Manage LLM providers (TUI or subcommands: `add`, `auth`, `list`, `get`, `remove`, `enable`, `disable`) |
| `netclaw model` | Manage model assignments (TUI or subcommands) |
| `netclaw mcp` | Manage MCP servers (`add`, `auth`, `list`, `get`, `remove`, `enable`, `disable`) |
| `netclaw secrets` | Manage encrypted secrets |

### Reminders

| Command | Purpose |
|---------|---------|
| `netclaw reminder list` | List all reminders |
| `netclaw reminder create` | Create a reminder |
| `netclaw reminder show <id>` | Show reminder details |
| `netclaw reminder cancel <id>` | Delete a reminder |
| `netclaw reminder enable <id>` | Enable a disabled reminder |
| `netclaw reminder disable <id>` | Disable a reminder |
| `netclaw reminder import <file>` | Import reminders from file |
| `netclaw reminder validate <file>` | Validate reminder file without importing |
| `netclaw reminder ui` | Reminder creation TUI |

### Maintenance

| Command | Purpose |
|---------|---------|
| `netclaw update` | Check for and install updates |
| `netclaw --version` | Show CLI version |

## Health Endpoints

| Endpoint | Purpose |
|----------|---------|
| `http://127.0.0.1:5199/api/health/ready` | Readiness probe |
| `http://127.0.0.1:5199/api/health/status` | Full runtime status JSON |
| `http://127.0.0.1:5199/api/sessions` | Active session list |

## Cross-References

- Memory tool usage: read `memory-usage`
- Troubleshooting and diagnostics: read `self-diagnostics`
- Identity file management: read `identity-management`
- Creating new skills: read `skill-authoring`
- Search behavior and citation policy: read `search-citation`
