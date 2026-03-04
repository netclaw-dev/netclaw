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

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned at `10.0.102`
  via `global.json`, `rollForward: major`)
- A local [Ollama](https://ollama.com/) instance (default provider), or an
  OpenRouter API key

### 1. Install prebuilt binaries (release feed)

Linux (installs CLI + daemon to `~/.netclaw/bin` by default):

```bash
curl -sSL https://feeds.netclaw.dev/install.sh | bash
```

Common Linux variants:

```bash
# Install only the CLI
curl -sSL https://feeds.netclaw.dev/install.sh | bash -s -- cli

# Install only the daemon
curl -sSL https://feeds.netclaw.dev/install.sh | bash -s -- daemon

# Pin a specific version
NETCLAW_VERSION=0.1.0 curl -sSL https://feeds.netclaw.dev/install.sh | bash
```

Windows (installs to `%LOCALAPPDATA%\Programs\netclaw` by default):

```powershell
iwr -useb https://feeds.netclaw.dev/install.ps1 | iex
```

To pass `-Component`, `-InstallDir`, or `-Version` on Windows, save and run
the script locally:

```powershell
$script = Join-Path $env:TEMP "netclaw-install.ps1"
iwr -useb https://feeds.netclaw.dev/install.ps1 -OutFile $script
& $script -Component all -Version 0.1.0
```

### 2. Build and publish (from source)

```bash
# Build everything
dotnet build Netclaw.slnx

# Publish both binaries to a shared output folder
dotnet publish src/Netclaw.Daemon/Netclaw.Daemon.csproj -c Release -o ./out
dotnet publish src/Netclaw.Cli/Netclaw.Cli.csproj -c Release -o ./out
```

### 3. Make the CLI available

Either add the output folder to your PATH:

```bash
export PATH="$PWD/out:$PATH"
```

Or point the CLI at the daemon binary explicitly:

```bash
export NETCLAW_DAEMON_PATH="$PWD/out/netclawd"
alias netclaw="$PWD/out/netclaw"
```

### 4. Configure an LLM provider

Run the guided setup wizard:

```bash
netclaw init
```

Or create the config manually. The daemon reads layered config from
`~/.netclaw/config/`:

```bash
mkdir -p ~/.netclaw/config
```

**`~/.netclaw/config/netclaw.json`** — base settings (minimal Ollama example):

```json
{
  "configVersion": 1,
  "Providers": {
    "local-ollama": {
      "Type": "ollama",
      "Endpoint": "http://localhost:11434"
    }
  },
  "Models": {
    "Main": { "Provider": "local-ollama", "ModelId": "qwen3:30b" }
  }
}
```

**`~/.netclaw/config/secrets.json`** — credentials (Slack tokens, API keys):

```json
{
  "Providers": {
    "openrouter": { "ApiKey": "sk-or-v1-..." }
  },
  "Slack": {
    "BotToken": "xoxb-...",
    "AppToken": "xapp-..."
  }
}
```

```bash
chmod 600 ~/.netclaw/config/secrets.json
```

All settings can also be overridden via environment variables using the
`NETCLAW_` prefix with double-underscore separators for nested keys:

```bash
export NETCLAW_Providers__local-ollama__Endpoint=http://localhost:11434
export NETCLAW_Models__Main__ModelId=qwen3:8b
```

### 5. Validate configuration

```bash
netclaw doctor          # Check config schema, provider connectivity, secrets
netclaw doctor --fix    # Auto-apply safe fixes
```

### 6. Run

```bash
# Start the daemon (background process)
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

# Start without rebuilding image (useful after pre-building or in CI)
SMOKE_BUILD=0 scripts/smoke/up.sh

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
- Runs on every pull request update.
- Uses Docker Buildx + GitHub Actions cache for smoke image layers.
- Always uploads `smoke-logs-*` artifact (including `check.log`, container
  logs, compose status, daemon log, PID snapshot) for debugging.

## Operations Runbooks

- Daemon upgrade and rollback planning: `docs/runbooks/daemon-upgrade.md`
- Behavior debugging and telemetry triage: `docs/runbooks/behavior-debugging.md`

## Integrations

- Integration docs index: `docs/integrations/README.md`
- Slack Socket Mode setup: `docs/integrations/slack-socket-mode.md`
- Slack ACL policy model: `docs/integrations/slack-acl-policy.md`

## Configuration

Configuration is layered — later sources override earlier ones:

1. `~/.netclaw/config/netclaw.json` — base settings
2. `~/.netclaw/config/secrets.json` — credential overlay (`chmod 600`)
3. `NETCLAW_*` environment variables — highest priority

Directories are created automatically on first run.

### `~/.netclaw/` Directory Layout

```
~/.netclaw/
├── netclaw.pid                # daemon PID file
├── netclaw.db                 # SQLite persistence (default)
├── config/
│   ├── netclaw.json           # base settings
│   └── secrets.json           # credentials (chmod 600)
├── identity/                  # system prompt layers
│   ├── SOUL.md
│   ├── AGENTS.md
│   └── TOOLING.md
├── skills/                    # system and user skills
├── memories/                  # file-backed cross-session memory
├── sessions/
└── logs/
    ├── daemon.log
    └── sessions/
```

### Persistence

Persistence config belongs in `netclaw.json` (not `secrets.json`). SQLite path
is local file state, not a secret.

```json
{
  "Persistence": {
    "Provider": "Sqlite",
    "Sqlite": {
      "Path": "/home/your-user/.netclaw/netclaw.db",
      "AutoMigrate": true
    }
  }
}
```

### Defaults (No Config Files)

When no config files exist, the daemon defaults to:

- **Provider:** `local-ollama` at `http://localhost:11434`
- **Main model:** `qwen3:30b` (32K context)
- **Persistence:** SQLite at `~/.netclaw/netclaw.db`
- **Search:** DuckDuckGo (no API key required)
- **Slack:** disabled

## CLI Reference

```
netclaw chat                  Interactive TUI chat session
netclaw -p "prompt"           Headless single-prompt mode
netclaw daemon start          Start the daemon as a background process
netclaw daemon stop           Gracefully stop the daemon (SIGTERM)
netclaw daemon status         Show daemon PID and uptime
netclaw daemon install        Install as systemd user service (Linux)
netclaw daemon uninstall      Remove systemd user service (Linux)
netclaw status                Runtime status from daemon health JSON endpoint
netclaw config                Configuration management (planned)
netclaw init                  First-run setup wizard (interactive TUI)
netclaw doctor                Configuration diagnostics (schema + secrets syntax)
netclaw doctor --fix          Auto-apply safe configuration fixes
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

## License

Netclaw is source-available under AGPLv3 with Commons Clause.
See `LICENSE` and `LICENSE-AGPL-3.0.txt`.
