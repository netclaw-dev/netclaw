# PRD-004: CLI Onboarding and Configuration

## Status

- State: Draft for execution (revised)
- Owner: Netclaw engineering
- Date: 2026-02-21
- Revised: 2026-02-21 (two-phase onboarding, expanded command surface, TUI
  commands, Cocona + Termina frameworks)
- Revised: 2026-02-23 (daemon + thin client split, daemon management commands,
  offline vs daemon-required command categorization)
- Depends on: `PRD-001`, `PRD-002`

## Goal

Provide a first-class operator CLI to bootstrap, validate, and troubleshoot
Netclaw. The CLI is the **primary operator interface during MVP** — all
workflows that will eventually appear in the ops console (PRD-003) must be
accessible via CLI first.

## Product Outcome

An owner can go from empty config to safe runtime startup and ongoing
diagnostics using CLI commands and guided output.

## CLI Architecture

Netclaw ships as two binaries (see PRD-001 for full architecture):

- **`Netclaw.Daemon`** — always-on service owning all agent logic, persistence,
  tools, and channels. Exposes SignalR hub at `/hub/session` and health endpoint.
- **`Netclaw.Cli`** — lightweight client. Connects to the daemon over SignalR
  for commands that need runtime state. Some commands work offline.

### CLI Framework

- **Simple arg routing** in `Program.cs` for command selection (Cocona is archived
  as of Dec 2025 — replaced with direct `args[0]` routing)
- **Termina 0.5.1** for interactive TUI commands (`netclaw init`, `netclaw chat`)
- All other commands use plain console output
- Commands that need the daemon connect via `Microsoft.AspNetCore.SignalR.Client`
- If the daemon isn't running and a command requires it, print an error with
  instructions: `Daemon not running. Start it with: netclaw daemon start`
- Configuration files contain API keys/secrets — config read/write commands
  operate on local files directly, never query config over the wire

## Two-Phase Onboarding

### Phase 1: CLI Wizard (`netclaw init`)

Technical setup, no LLM required. `netclaw init` runs as a **lightweight mode**
— no Akka actor system, no persistence, no SignalR. Only config services are
booted. Provider testing uses direct DI service calls (`ChatClientFactory`),
not REST endpoints.

The wizard is **reentrant** — re-running `netclaw init` detects existing config
and shows a section dashboard with status per section. Each section is
independently enterable for modification. First-run guides linearly through
all steps.

Steps:

1. LLM provider configuration (endpoint URL, API key or OAuth device flow,
   model selection, connectivity test via direct HTTP to provider)
2. Slack app setup (bot token, app token for Socket Mode)
3. ACL bootstrap (owner identity, initial channel rules)
4. MCP server configuration (optional — Memorizer recommended)
5. Exposure mode selection (local-only default)
6. Health check (verify Slack connection, LLM reachability, MCP connectivity)

### Phase 2: Conversational Personality Bootstrap (first `netclaw chat`)

Agent-driven setup, requires running LLM:

1. "Hi, I'm Netclaw. Let me learn about you and your setup."
2. Ask about projects to register (repo paths on disk)
3. Discover environment capabilities (scan for installed tools)
4. Write PERSONALITY.md, USER.md, environment inventory
5. Confirm readiness

Phase 2 is triggered automatically on first `netclaw chat` if personality files
don't exist. It can also be re-triggered via CLI (`netclaw personality reset`).

## Command Surface (MVP)

### Daemon Management (no daemon required)

- `netclaw daemon start` — start the daemon as a background process
- `netclaw daemon stop` — stop the running daemon
- `netclaw daemon status` — check if daemon is running, show PID and uptime
- `netclaw daemon install` — register as a systemd user service
  (`~/.config/systemd/user/netclaw.service`, no sudo). Supports
  `loginctl enable-linger` for surviving logout.
- `netclaw daemon uninstall` — remove systemd user service registration

### TUI-Interactive Commands (Termina, daemon required)

- `netclaw chat` — interactive agent prompt. Pure thin client connecting to the
  daemon over SignalR. Renders `SessionOutput` stream, sends `ChannelInput`.
  Session entity key: `tui/{uuid}`. See TUI-001 wireframes.

### TUI-Interactive Commands (Termina, offline)

- `netclaw init` — guided first-time setup wizard (7-step TUI wizard). Reads
  and writes local config files directly. No daemon required.

### Onboarding and Configuration (Plain CLI, offline)

- `netclaw config show|validate` — display/validate current configuration
- `netclaw personality reset` — re-trigger conversational personality setup
- `netclaw project list|add|remove` — project registry management (local files)
- `netclaw environment scan|show` — capability self-discovery (scans local system)

### Diagnostics (Plain CLI, offline)

- `netclaw doctor` — validate config files, check daemon reachability, test
  provider connectivity, report system health, and flag unsafe trust-policy
  combinations such as unrestricted `public` or `team` audience profiles

### Security and Policy (daemon required)

- `netclaw acl validate|test|explain` — ACL validation and testing against the
  running policy engine

### Sessions (daemon required)

- `netclaw session list|inspect|compact` — session management and inspection

### Memory (daemon required)

- `netclaw memory show` — display current agent memory

### Scheduling (daemon required)

- `netclaw schedule list|show|pause|resume|delete` — scheduled task management

### Tools and MCP (daemon required)

- `netclaw tools list|policy` — tool availability and policy display
- `netclaw mcp list|validate|test` — MCP server management

### Testing (daemon required)

- `netclaw test smoke [--provider ollama]` — end-to-end smoke test through daemon

## Requirements

### CLI-001 Onboarding

`init` creates baseline config and highlights required secrets and policy items.
Onboarding captures all Phase 1 setup items in a stepwise flow.

### CLI-001A Guided Setup Wizard

`netclaw init` SHALL support an interactive guided onboarding flow that:

1. Captures LLM provider configuration (OpenRouter default, OAuth or API key)
2. Configures Slack Socket Mode credentials (bot token + app token)
3. Scaffolds ACL in default-deny mode with owner identity
4. Optionally configures MCP servers (Memorizer recommended)
5. Selects exposure mode (local default)
6. Runs final validation and prints next-step run commands

### CLI-002 Validation

`config validate` and `acl validate` provide structured errors with file path
and property location.

### CLI-003 Explainability

`acl explain` and `acl test` show effective policy decisions for sample inputs.

### CLI-004 Runtime Diagnostics

`status` and `doctor` summarize connectivity, persistence, policy health, MCP
server health, trust-context policy readiness, and scheduled task status.

When trust-context policy is configured, diagnostics SHALL surface:

- whether strict-default trust-policy fallback is active
- the resolved `public`, `team`, and `personal` audience-profile scopes
- unsafe unrestricted profile combinations
- sandbox-shell readiness when `ShellMode` resolves to `SandboxOnly`

### CLI-005 Session Operations

`session inspect` exposes current state, last activity, compaction metadata,
and active tool grants for the session.

### CLI-006 Safe Defaults

Commands default to read-only behavior unless explicit write/apply flags are
provided.

### CLI-007 Onboarding Resume

The onboarding flow SHALL be resumable and indicate which setup steps are
completed, pending, or invalid.

### CLI-008 Project Registration

`project add` SHALL register a project with:
- repo path on disk
- optional AGENTS.md path (defaults to `{repo}/AGENTS.md`)
- optional associated Slack channels
- capabilities (has tests, has CI, language/framework)

### CLI-009 Environment Discovery

`environment scan` SHALL discover:
- Installed CLIs: `claude`, `opencode`, `git`, `gh`, `dotnet`, `node`
- Git credential availability (for which hosts)
- .NET SDK version
- MCP server reachability
- Registered project paths validity

Results are persisted to the environment inventory file.

### CLI-010 TUI Commands

`netclaw init` and `netclaw chat` SHALL use Termina 0.5.1 for interactive TUI
rendering. All other commands SHALL use plain console output. TUI commands SHALL
launch Termina as a hosted service within the mode-selected host builder.

### CLI-011 Chat Thin Client

`netclaw chat` SHALL connect to the running daemon over SignalR and provide an
interactive TUI for agent conversations. The TUI SHALL:

- Connect to the daemon's SignalR hub at `http://127.0.0.1:5199/hub/session`
- Create a session via the hub and receive a session ID
- Send `ChannelInput` messages via SignalR
- Subscribe to `SessionOutput` stream for rendering
- Render session output as streaming text via StreamingTextNode
- Display tool invocation status inline (completed with duration, in-progress
  with spinner)
- Show model name, token usage, and context percentage in status bar
- Print a clear error if the daemon is not running

### CLI-012 Daemon Management

The CLI SHALL provide commands to manage the daemon lifecycle:

- `netclaw daemon start` SHALL start the daemon as a background process
- `netclaw daemon stop` SHALL stop the running daemon gracefully
- `netclaw daemon status` SHALL report daemon state (running/stopped, PID, uptime)
- `netclaw daemon install` SHALL register as a systemd user service (Linux) or
  LaunchAgent (macOS). No sudo required — uses `systemctl --user` and
  `loginctl enable-linger` on Linux.
- `netclaw daemon uninstall` SHALL remove the service registration

### CLI-013 Daemon Process

The daemon (`Netclaw.Daemon`) SHALL run as a standalone service with Slack Socket
Mode adapter, Akka actor system, scheduled task timers, SignalR hub, and health
endpoints. No TUI rendering. This is the primary production entry point.

## UX Requirements

- human-readable output by default, machine-friendly JSON opt-in (`--json`)
- explicit exit codes for automation
- no hidden side effects for diagnostic commands

## Acceptance Criteria

1. CLI spec covers onboarding, validation, policy diagnostics, and session ops.
2. Every high-risk command has confirmation or explicit `--yes` semantics.
3. Error output includes remediation guidance.
4. Fresh install reaches a runnable baseline in one guided flow.
5. Personality bootstrap triggers automatically on first conversation.
6. Environment scan discovers and persists capability inventory.
7. Project registration persists project registry to disk.

## Cross-References

- MVP scope: PRD-001
- Security validation: PRD-002
- Ops console (future UI): PRD-003
- Provider setup: PRD-005
- MCP setup: PRD-006
- Memory and personality: PRD-007
- Scheduling: PRD-008
- Daemon architecture: SPEC-011
- TUI wireframes: TUI-001
