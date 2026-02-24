# Netclaw

Netclaw is a Slack-connected homelab assistant built on top of a minimal
actor-driven session framework called Akka.Agents.

## Architecture

Netclaw uses a **daemon + thin client** architecture:

- **`netclawd`** (`src/Netclaw.Daemon/`) — always-on daemon process hosting
  the Akka actor system, LLM sessions, tool execution, and persistence.
  Exposes a SignalR hub at `/hub/session` for remote clients.

- **`netclaw`** (`src/Netclaw.Cli/`) — thin CLI client for interactive chat,
  daemon management, and configuration. Connects to the daemon via SignalR.

## Quick Start

```bash
# Build everything
dotnet build Netclaw.slnx

# Start the daemon
netclaw daemon start

# Check daemon status
netclaw daemon status

# Interactive chat (connects to running daemon)
netclaw chat

# Stop the daemon
netclaw daemon stop
```

## Developer Smoke Sandbox (Docker)

Developer-only integration sandbox for daemon lifecycle and gateway checks.
This is intentionally script-driven (not a user-facing `netclaw test smoke`
command yet).

```bash
# Start sandbox (build local image + start Ollama + pull tiny model)
scripts/smoke/up.sh

# Run smoke checks (daemon start/status/health/stop)
scripts/smoke/check.sh

# Tear down sandbox
scripts/smoke/down.sh

# Optional: remove volumes too
SMOKE_REMOVE_VOLUMES=1 scripts/smoke/down.sh
```

Optional model override:

```bash
SMOKE_OLLAMA_MODEL=qwen2:0.5b scripts/smoke/up.sh
```

Useful timeout overrides for `scripts/smoke/check.sh`:

```bash
# Wait up to 20 minutes for model pull/bootstrap (default: 1200)
INIT_TIMEOUT_SECONDS=1200 scripts/smoke/check.sh

# Per-command timeout inside sandbox (default: 120)
STEP_TIMEOUT_SECONDS=120 scripts/smoke/check.sh
```

### CI Smoke Workflow

`smoke_sandbox` is available in GitHub Actions:

- Runs manually via `workflow_dispatch`.
- Runs on PRs labeled `smoke`.
- Always uploads `smoke-logs-*` artifact (container logs, compose status,
  daemon log, PID snapshot) for debugging.

## CLI Reference

```
netclaw chat                  Interactive TUI chat session
netclaw -p "prompt"           Headless single-prompt mode
netclaw daemon start          Start the daemon as a background process
netclaw daemon stop           Gracefully stop the daemon (SIGTERM)
netclaw daemon status         Show daemon PID and uptime
netclaw daemon install        Install as systemd user service (Linux)
netclaw daemon uninstall      Remove systemd user service (Linux)
netclaw config                Configuration management (planned)
netclaw init                  First-run setup wizard (planned)
netclaw doctor                Health checks (planned)
```

### Daemon Binary Discovery

The `daemon start` command locates `netclawd` by:

1. `NETCLAW_DAEMON_PATH` environment variable (explicit path)
2. Same directory as the `netclaw` CLI binary

### systemd Service

On Linux, `netclaw daemon install` creates a user-level systemd service:

```bash
netclaw daemon install        # Creates ~/.config/systemd/user/netclaw.service
systemctl --user start netclaw
systemctl --user status netclaw
```

## Current Focus

MVP target: run Netclaw on `pi1`, reply in Slack threads, persist sessions
across restarts, and compact long conversations without losing context.

Primary constraints:

- Gall's Law: build the simplest working system first
- single-process runtime for MVP
- .NET 10 runtime baseline
- default-deny ACL and explicit policy checks
- session identity is Slack thread: `{channelId}/{threadTs}`
- MCP server integration is included in MVP scope
- protobuf-net for persistence types (no direct serialization of
  `Microsoft.Extensions.AI` message types)

## Project Structure

- Solution: `Netclaw.slnx`
- Daemon: `src/Netclaw.Daemon/Netclaw.Daemon.csproj` (Web API host, `netclawd`)
- CLI: `src/Netclaw.Cli/Netclaw.Cli.csproj` (thin client, `netclaw`)
- Actors: `src/Netclaw.Actors/` (session management, persistence, tools)
- Configuration: `src/Netclaw.Configuration/` (paths, providers, models)
- Channels: `src/Netclaw.Channels/` (channel abstractions)

Build and test:

```bash
dotnet build Netclaw.slnx
dotnet test Netclaw.slnx
dotnet slopwatch analyze
```

## Planning Artifacts

- `docs/prd/` - product requirements and acceptance criteria
- `docs/spec/` - engineering specifications and contracts
- `docs/ui/` - management UI mockups
- `openspec/specs/` - capability specs for ongoing evolution
- `openspec/changes/` - change proposals, design notes, and execution tasks

## OpenSpec Workflow

OpenSpec is initialized for OpenCode in this repository.

Common commands:

- `/opsx:new`
- `/opsx:continue`
- `/opsx:ff`
- `/opsx:apply`
- `/opsx:verify`
- `/opsx:archive`

CLI equivalents are available via `openspec --help`.

Netclaw-specific helper skills are available at:

- `.opencode/skills/netclaw-openspec-planning/SKILL.md`
- `.opencode/skills/netclaw-openspec-milestones/SKILL.md`

## RALPH Loop

RALPH infrastructure is available in this repo and tuned for OpenSpec-traceable
execution.

- `ralph-opencode.sh` - OpenCode loop runner
- `ralph.sh` - Claude Code loop runner
- `.claude/skills/ralph-loop.md` - loop discipline with OpenSpec gates
- `.claude/skills/ralph-run-diagnostics.md` - process diagnostics
- `.claude/skills/ralph-output-adversarial-review.md` - adversarial review
- `IMPLEMENTATION_PLAN.md` - RALPH task queue
- `BACKLOG_PARKING_LOT.md` - parked items requiring human decisions

## Bootstrap Docs

Repository operating guidance lives in:

- `AGENTS.md`
- `PROJECT_CONTEXT.md`
- `TOOLING.md`

These files define how planning and implementation work should be routed.
