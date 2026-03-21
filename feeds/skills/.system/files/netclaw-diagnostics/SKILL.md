---
name: netclaw-diagnostics
description: "Netclaw diagnostics and session debugging. Read when the user wants to understand what happened in a Netclaw session, why a tool failed, why capabilities are missing, or whether daemon/memory/config health is degraded."
metadata:
  author: netclaw
  version: "0.8.0"
  triggers: what happened in this session | debug this session | why did netclaw do that | why did this tool fail | missing tools in this session | session timeout | daemon unhealthy | memory degraded | inspect netclaw logs
---

# Netclaw Diagnostics

Use this skill when the user's intent is to diagnose Netclaw itself:

- what happened in this session or thread
- why a tool call failed, timed out, or returned something odd
- why expected capabilities/tools were missing
- why daemon, memory, provider, MCP, or config health looks degraded
- how to inspect logs, runtime state, and health endpoints safely

If the user is primarily asking what Netclaw can do or which built-in command or
tool to use, read `netclaw-manual` instead.

## Behavioral Triggers

Read and follow this skill proactively when ANY of these occur:

- Connection failure (provider or MCP errors)
- Session issues (timeouts, unresponsiveness, unexpected endings)
- Missing tools (discovery failures, MCP disconnect)
- User asks to debug a Netclaw session or explain what happened
- User-reported daemon/config errors
- Memory degradation or high checkpoint backlog

## Quick Reference

| What | Command / Path |
|------|---------------|
| Offline config validation | `netclaw doctor` |
| Daemon status (requires daemon) | `netclaw status` |
| Daemon lifecycle | `netclaw daemon start\|stop\|status` |
| Health endpoint | `curl http://127.0.0.1:5199/api/health/ready` |
| Full status JSON | `curl http://127.0.0.1:5199/api/health/status` |
| Active sessions | `curl http://127.0.0.1:5199/api/sessions` |
| MCP server status | `netclaw mcp list` (daemon required) |
| Provider list | `netclaw provider list` |
| Model configuration | `netclaw model list` |
| Memory runbook | `docs/runbooks/memory-health-and-evals.md` |

For a complete catalog of Netclaw commands and capabilities, read
`netclaw-manual`.

## Configuration Files

| File | Purpose |
|------|---------|
| `~/.netclaw/config/netclaw.json` | Base configuration |
| `~/.netclaw/config/secrets.json` | Credentials overlay |

Use `file_read` to inspect these files. Never log/display API keys.

## netclaw doctor

Primary diagnostic tool. It always validates local config and, when the daemon is reachable, prefers daemon-reported MCP auth/connectivity state over offline guesses.

```bash
netclaw doctor
netclaw doctor --format json
netclaw doctor --fix
netclaw doctor --fix --dry-run
```

Important memory checks include:

- SQLite provisioning check
- Memory checkpoint health check (pending checkpoint backlog)

## Session-First Debug Flow

When the user asks "what happened in this session?" or "why did Netclaw do X?"
start with the narrowest useful evidence:

1. identify the active session or thread
2. inspect `netclaw status` and relevant health endpoints
3. inspect daemon/session logs for the matching turn or time window
4. only then widen to provider, MCP, memory, or config checks

Prefer explaining the observed failure chain over dumping raw logs.

## Daemon Status For Memory

Use `netclaw status` and inspect `memory`:

- `provider` (expected `sqlite` for default memory path)
- `status` (`healthy`, `degraded`, `unavailable`)
- `databasePath`
- `pendingCheckpoints`

If `pendingCheckpoints` grows persistently, inspect daemon logs and curation
worker activity.

## Logs

| Log | Location |
|-----|----------|
| Daemon | `~/.netclaw/logs/daemon-{yyyy-MM-dd}.log` |
| Session | `~/.netclaw/logs/sessions/{yyyyMMdd-HHmmss}_{session-id}.log` |
| Crash | `~/.netclaw/logs/crash-{yyyyMMdd-HHmmss}.log` |

## Common Issues

| Symptom | Check |
|---------|-------|
| No LLM responses | `netclaw doctor`; verify provider credentials |
| Missing tools | `netclaw mcp list`; if auth state is unknown, restore daemon connectivity first, then inspect daemon logs |
| Memory recall degraded | `netclaw status` memory section; run `netclaw doctor` |
| Pending checkpoints keep rising | `Memory Checkpoint Health` warning + daemon logs |
| Daemon won't start | stale PID, crash logs, config JSON validity |
| One session behaved strangely | session-specific log + matching daemon turn trace |

## Reporting Bugs

Include:

- `netclaw --version`
- relevant log excerpts (redacted)
- `netclaw doctor --format json` output
- reproduction steps and expected vs actual behavior
