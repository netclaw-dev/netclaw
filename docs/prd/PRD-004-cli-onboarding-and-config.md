# PRD-004: CLI Onboarding and Configuration

## Status

- State: Draft for execution (revised)
- Owner: Netclaw engineering
- Date: 2026-02-21
- Revised: 2026-02-21 (two-phase onboarding, expanded command surface, TUI
  commands, Cocona + Termina frameworks)
- Depends on: `PRD-001`, `PRD-002`

## Goal

Provide a first-class operator CLI to bootstrap, validate, and troubleshoot
Netclaw. The CLI is the **primary operator interface during MVP** — all
workflows that will eventually appear in the ops console (PRD-003) must be
accessible via CLI first.

## Product Outcome

An owner can go from empty config to safe runtime startup and ongoing
diagnostics using CLI commands and guided output.

## CLI Framework

- **Simple arg routing** in `Program.cs` for mode selection (Cocona is archived
  as of Dec 2025 — replaced with direct `args[0]` routing)
- **Termina 0.5.1** for interactive TUI commands (`netclaw init`, `netclaw chat`)
- All other commands use plain console output
- `netclaw run` is the explicit daemon entry point (Slack + timers + health
  endpoints, no TUI)
- All CLI modes are in-process — no REST client in Phase 1
- Configuration is privileged local file I/O, never exposed over the wire
  (contains API keys/secrets)

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

1. LLM provider configuration (endpoint URL, API key, model selection,
   connectivity test via direct HTTP to provider)
2. Slack app setup (bot token, app token for Socket Mode)
3. PostgreSQL connection string
4. ACL bootstrap (owner identity, initial channel rules)
5. MCP server configuration (optional — Memorizer recommended)
6. Exposure mode selection (local-only default)
7. Health check (verify Slack connection, DB connection, LLM reachability)

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

### TUI-Interactive Commands (Termina)

- `netclaw init` — guided first-time setup wizard (7-step TUI wizard)
- `netclaw chat` — interactive agent prompt with streaming responses, tool
  activity display, and MCP status. Hosts actor system in-process. Session
  entity key: `tui/{uuid}`. See TUI-001 wireframes.

### Daemon Mode

- `netclaw run` — start daemon mode (Slack Socket Mode + Akka actor system +
  scheduled task timers + health endpoints). No TUI. Primary production entry
  point.

### Onboarding and Configuration (Plain CLI)

- `netclaw config show|validate` — display/validate current configuration
- `netclaw personality reset` — re-trigger conversational personality setup

### Security and Policy

- `netclaw acl validate|test|explain` — ACL validation, testing, explanation
- `netclaw gateway status|doctor` — connectivity, exposure, health diagnostics

### Sessions

- `netclaw session list|inspect|compact` — session management and inspection

### Memory and Projects

- `netclaw project list|add|remove` — project registry management
- `netclaw environment scan|show` — capability self-discovery and display
- `netclaw memory show` — display current agent memory files

### Scheduling

- `netclaw schedule list|show|pause|resume|delete` — scheduled task management

### Tools and MCP

- `netclaw tools list|policy` — tool availability and policy display
- `netclaw mcp list|validate|test` — MCP server management

### Testing

- `netclaw test smoke [--provider ollama]` — provider smoke test

## Requirements

### CLI-001 Onboarding

`init` creates baseline config and highlights required secrets and policy items.
Onboarding captures all Phase 1 setup items in a stepwise flow.

### CLI-001A Guided Setup Wizard

`netclaw init` SHALL support an interactive guided onboarding flow that:

1. Captures LLM provider configuration (OpenRouter default)
2. Configures Slack Socket Mode credentials (bot token + app token)
3. Configures PostgreSQL connection string
4. Scaffolds ACL in default-deny mode with owner identity
5. Optionally configures MCP servers (Memorizer recommended)
6. Selects exposure mode (local default)
7. Runs final validation and prints next-step run commands

### CLI-002 Validation

`config validate` and `acl validate` provide structured errors with file path
and property location.

### CLI-003 Explainability

`acl explain` and `acl test` show effective policy decisions for sample inputs.

### CLI-004 Runtime Diagnostics

`gateway status` and `gateway doctor` summarize connectivity, persistence,
policy health, MCP server health, and scheduled task status.

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

### CLI-011 Local Chat Adapter

`netclaw chat` SHALL host the full actor system in-process and provide a local
input adapter for MVP validation. The TUI adapter SHALL:

- Produce `SendUserMessage` commands with entity key `tui/{sessionId}`
- Render session broadcasts as streaming text via StreamingTextNode
- Display tool invocation status inline (completed with duration, in-progress
  with spinner)
- Show MCP server connectivity status in the status bar

### CLI-012 Daemon Entry Point

`netclaw run` SHALL start the daemon process with Slack Socket Mode adapter,
Akka actor system, scheduled task timers, and health endpoints. No TUI
rendering. This is the primary production entry point.

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
