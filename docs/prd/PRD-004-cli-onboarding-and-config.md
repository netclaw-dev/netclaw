# PRD-004: CLI Onboarding and Configuration

## Status

- State: Draft for execution (revised)
- Owner: Netclaw engineering
- Date: 2026-02-21
- Revised: 2026-02-21 (two-phase onboarding, expanded command surface, TUI
  commands, Cocona + Termina frameworks)
- Revised: 2026-02-23 (daemon + thin client split, daemon management commands,
  offline vs daemon-required command categorization)
- Revised: 2026-05-24 (bootstrap-only `init`, domain-oriented `config`,
  init-owned identity re-entry, explicit reset flow)
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

## Bootstrap and Ongoing Configuration

### Bootstrap: `netclaw init`

Technical setup, no daemon required. `netclaw init` runs as a lightweight
offline mode: no Akka actor system, no SignalR, no runtime session host.
Provider testing uses direct DI service calls and local validation.

`netclaw init` is bootstrap-first and intentionally short.

Fresh-install flow:

1. LLM provider configuration (endpoint URL, API key or OAuth device flow,
   model selection, connectivity test)
2. Identity setup (workspaces directory, user name, timezone) with init-owned
   regeneration of `SOUL.md` and `TOOLING.md` plus non-destructive seeding of
   the deployment mission scaffold in `AGENTS.md`
3. Security posture (`Personal`, `Team`, `Public`)
4. Enabled Features for `Team` and `Public` only
5. Final validation / health check / next steps

Existing-install flow:

1. `Redo identity setup`
2. `Open configuration editor`
3. `Start over from scratch`
4. `Cancel`

`Start over from scratch` is owned by the existing-install init menu, not a
hidden flag. It opens a scope selector:

1. `Reset setup only`
2. `Full reset`
3. `Cancel`

Both destructive paths require double confirmation.

`Reset setup only` archives and recreates setup-owned state while preserving
working data such as the SQLite database, logs, projects, schedules,
environment, and skills. `Full reset` wipes the entire Netclaw home except the
installed binary payload.

### Ongoing Settings: `netclaw config`

`netclaw config` is the main post-install settings surface. It is a
domain-oriented Termina TUI, not a flat dump of raw config sections.

Top-level domains:

1. `Inference Providers`
2. `Models`
3. `Channels`
4. `Inbound Webhooks`
5. `Skill Sources`
6. `Search`
7. `Browser Automation`
8. `Telemetry & Alerting`
9. `Security & Access`

Command ownership stays explicit:

1. `netclaw init` owns bootstrap and identity re-entry
2. `netclaw config` owns normal post-install tuning
3. `netclaw provider` and `netclaw model` remain their canonical standalone
   entrypoints and may be routed to from `netclaw config`

## Command Surface (MVP)

### Daemon Management (no daemon required)

- `netclaw daemon start` — start the daemon as a background process
- `netclaw daemon stop` — stop the running daemon
- `netclaw daemon status` — check if daemon is running, show PID and uptime
- `netclaw daemon install` — register as a systemd user service
  (`~/.config/systemd/user/netclaw.service`, no sudo). Supports
  `loginctl enable-linger` for surviving logout. Captures the operator's real
  shell `PATH` into `~/.netclaw/config/daemon.env` (loaded via `EnvironmentFile=`)
  so the daemon's shell tool resolves the same binaries the operator can.
- `netclaw daemon uninstall` — remove systemd user service registration and the
  captured `daemon.env`

### TUI-Interactive Commands (Termina, daemon required)

- `netclaw chat` — interactive agent prompt. Pure thin client connecting to the
  daemon over SignalR. Renders `SessionOutput` stream, sends `ChannelInput`.
  Session entity key: `tui/{uuid}`. If `netclaw.json` is absent, the command
  SHALL fail before contacting the daemon with
  `daemon not configured - please run netclaw init`. See TUI-001 wireframes.

### TUI-Interactive Commands (Termina, offline)

- `netclaw init` — guided bootstrap wizard plus rare existing-install
  identity/reset re-entry. Reads and writes local config files directly. No
  daemon required.
- `netclaw config` — domain-oriented post-install settings dashboard. Reads and
  writes local config files directly. No daemon required.
- `netclaw provider` — bare invocation launches interactive provider manager.
- `netclaw model` — bare invocation launches interactive model manager.

### Onboarding and Configuration (Plain CLI, offline)

- `netclaw personality reset` — re-trigger conversational personality setup
- `netclaw project list|add|remove` — project registry management (local files)
- `netclaw environment scan|show` — capability self-discovery (scans local system)

### Diagnostics (Plain CLI, offline)

- `netclaw status` — query daemon runtime health when initialized. If
  `netclaw.json` is absent, the command SHALL fail before contacting the daemon
  with `daemon not configured - please run netclaw init`.
- `netclaw doctor` — validate config files, check daemon reachability, test
  provider connectivity, report system health, and flag unsafe trust-policy
  combinations such as unrestricted `public` or `team` audience profiles. If
  `netclaw.json` is absent, the config-file diagnostic SHALL warn with
  `daemon not configured - please run netclaw init`.

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
2. Captures init-owned identity settings, regenerates `SOUL.md` / `TOOLING.md`,
   and seeds `AGENTS.md` only when absent
3. Selects security posture (`Personal`, `Team`, `Public`)
4. Continues into Enabled Features when posture is `Team` or `Public`
5. Runs final validation and prints next-step run commands

After successful setup, the initial chat SHALL discover operator context and
the deployment mission as separate concerns. Confirmed personality/operator
context is persisted to `SOUL.md`; confirmed mission, recurring workflows,
skill-selection rules, delegation practices, and review gates are persisted to
`AGENTS.md` without overwriting an existing playbook during wizard setup.

### CLI-001B Post-Install Configuration

`netclaw config` SHALL be the primary post-install settings surface. It SHALL:

1. Launch a domain-oriented dashboard
2. Route providers/models to their dedicated interactive managers
3. Group `Security Posture`, `Enabled Features`, `Audience Profiles`, and
   `Exposure Mode` under `Security & Access`
4. Refuse with a plain non-zero message directing the operator to
   `netclaw init` when no install exists

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

`netclaw doctor` SHALL include a **Chat Client** check that reports:

- **pass** — a real provider chat client is configured.
- **warn** — the No-Op chat client will be active because no explicit
  `Models:Main` provider/model is configured, no providers exist, or Main points
  to an unconfigured provider. Bound defaults do not count as configuration.
  The daemon starts in degraded mode and chat turns return a fixed recovery
  banner. Remediation references `netclaw init` for first-time provider/model
  setup, `netclaw model` when a provider already exists, and manual
  `netclaw.json` / `secrets.json` repair.
- **fail** — provider configuration is malformed (declared provider missing
  required credentials or `Type`, schema violation, explicit Fallback/Compaction
  role is incomplete, or explicit Fallback/Compaction points to an unconfigured
  provider); daemon startup will fail until resolved.

### CLI-005 Session Operations

`session inspect` exposes current state, last activity, compaction metadata,
and active tool grants for the session.

### CLI-006 Safe Defaults

Commands default to read-only behavior unless explicit write/apply flags are
provided.

### CLI-007 Existing-Install Re-entry

When `netclaw init` runs on an existing install, it SHALL present an explicit
action menu rather than silently re-entering the full bootstrap flow. Identity
re-entry remains init-owned; all normal configuration edits route to
`netclaw config`.

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

`netclaw init`, `netclaw config`, and `netclaw chat` SHALL use Termina 0.5.1
for interactive TUI rendering. Bare `netclaw provider` and `netclaw model`
SHALL also use Termina. All other commands SHALL use plain console output. TUI
commands SHALL launch Termina as a hosted service within the mode-selected host
builder.

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
  `loginctl enable-linger` on Linux. On Linux it SHALL capture the operator's
  real `PATH` (from the CLI process, without spawning a shell) into a
  netclaw-owned `EnvironmentFile` so the daemon's shell tool resolves
  operator-installed binaries; `netclaw doctor --fix` SHALL rehydrate it.
- `netclaw daemon uninstall` SHALL remove the service registration and the
  captured environment file

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
5. Existing installs can re-enter identity setup or open `netclaw config`
   without replaying full bootstrap.
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
