# Self-Diagnostics

<!-- description: How to check Netclaw configuration, validate connectivity, check logs, and report bugs -->

## Configuration Files

Netclaw's configuration lives in `~/.netclaw/config/`:

| File | Purpose |
|------|---------|
| `netclaw.json` | Base configuration (providers, models, tools, sessions) |
| `secrets.json` | Credentials overlay (API keys, tokens) |

Use `file_read` to inspect these files. **Never log or display API keys.**

Environment variables with `NETCLAW_` prefix override config file values.

## Checking Daemon Status

```bash
# Check if daemon is running
netclaw status

# Health endpoint (when daemon is running)
curl http://127.0.0.1:5199/api/health/ready

# Detailed status
curl http://127.0.0.1:5199/api/health/status
```

## Logs

- Daemon log: `~/.netclaw/logs/daemon-{yyyy-MM-dd}.log` (rotated daily)
- Session logs: `~/.netclaw/logs/sessions/{yyyyMMdd-HHmmss}_{session-id}.log` (sorted newest-first by name)
- Crash logs: `~/.netclaw/logs/crash-*.log`

Use `file_read` to inspect log files when debugging issues.

## MCP Server Connectivity

MCP servers are configured in `netclaw.json` under the `McpServers` section.
To validate connectivity:

1. Read `netclaw.json` to check configured servers
2. Use `search_tools` to verify tools from each MCP server are registered
3. If tools are missing, check daemon logs for MCP connection errors

## Common Issues

| Symptom | Check |
|---------|-------|
| No LLM responses | Verify provider API key in `secrets.json` and provider config in `netclaw.json` |
| Missing tools | Check MCP server configuration and daemon logs |
| Slow responses | Check model selection — larger models are slower |
| Session not persisting | Verify `Persistence` section in `netclaw.json` |

## Reporting Bugs

File issues at the project's GitHub repository. Include:
- Netclaw version (`netclaw --version`)
- Relevant log excerpts (redact API keys)
- Steps to reproduce
- Expected vs actual behavior
