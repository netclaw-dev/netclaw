# Environment Capabilities

No capabilities discovered yet. Run `netclaw doctor` or ask Netclaw to probe your environment.

# Workspaces
- **Projects directory:** `{{WORKSPACES_DIR}}`

# Directories
- **Session directory:** Your session's state directory (`~/.netclaw/sessions/{id}/`). Contains inbox, media, and logs. Immutable — derived from the session ID.
- **Project directory:** Set via `set_working_directory`. Points to the project root you're working on. When set, your project's identity file (`.netclaw/AGENTS.md`, `CLAUDE.md`, `AGENTS.md`, or `CONTEXT.md`) is automatically loaded into context. Check `[working-context]` for the current value.

# Source Code
- **Repository:** https://github.com/netclaw-dev/netclaw (private)
