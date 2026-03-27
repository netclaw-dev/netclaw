# PRD-001: Netclaw MVP

## Status

- State: Draft for execution (revised)
- Owner: Netclaw engineering
- Date: 2026-02-21
- Revised: 2026-02-21 (expanded product vision)
- Revised: 2026-02-23 (daemon + thin client split)

## Problem Statement

The operator needs an always-on autonomous operations agent running on homelab
infrastructure that can answer questions through Slack, remember context across
restarts, manage its own schedule, discover and use local tools, maintain
awareness of its environment, and modify its own configuration through
conversation — all without requiring a complex distributed deployment.

## Product Goal

Deliver a minimal but dependable autonomous operations agent that is
actor-driven, persistence-backed, memory-aware, tool-capable, and
safe-by-default. Netclaw is not just a chat assistant — it is an autonomous
operations platform that can monitor, react, investigate, delegate work, and
manage its own schedule.

## Key Architectural Insight

Everything is just a message arriving at a session actor with context-specific
instructions. The input source (Slack, webhook, timer, future web UI) is
irrelevant — the differentiator is the instructions attached to the context.

All external clients — TUI, CLI commands, future web UI — connect to the daemon
over SignalR. The daemon owns all agent logic, tool execution, persistence, and
channel management. Clients are thin presentation layers.

| Input Source          | Delivery Mechanism                          | MVP? |
|-----------------------|---------------------------------------------|------|
| Local TUI             | `netclaw chat` → SignalR to daemon          | Yes  |
| User @mention         | Slack Socket Mode (in daemon)               | Yes  |
| Scheduled task        | Internal timer (in daemon)                  | Yes  |
| CLI commands          | `netclaw <cmd>` → SignalR/HTTP to daemon    | Yes  |
| Ambient channel alert | Slack Socket Mode (require_mention: false)  | No   |
| Webhook (GitHub, CI)  | HTTP via Tailscale Serve / Cloudflare Tunnel| No   |
| Web UI (future)       | WebSocket / HTTP                            | No   |

## MVP Success Criteria

1. Netclaw replies in the same Slack thread where the user interacts.
2. Session context survives process restarts.
3. Long sessions compact context while preserving task continuity.
4. Unauthorized interactions are denied by policy.
5. Operator can configure and validate system behavior without source edits.
6. Agent maintains personality, project registry, and environment awareness
   across sessions.
7. Agent can use local tools (web search, shell, GitHub CLI) through
   policy-gated access.
8. Agent can create and manage scheduled tasks through conversation.
9. Agent can discover its own capabilities (installed tools, credentials,
   host info).
10. Agent can modify its own configuration through conversation.
11. MCP integration provides external memory (Memorizer) and tool capabilities.

## Daemon + Thin Client Architecture

Netclaw is split into two binaries following the same pattern as OpenClaw,
IronClaw, and PicoClaw:

- **`Netclaw.Daemon`** — always-on background service. Owns the Akka actor
  system, persistence, tool execution, Slack adapter, scheduled tasks, SignalR
  hub, and health endpoints. Runs as a user-level systemd service or foreground
  process.
- **`Netclaw.Cli`** — lightweight CLI and TUI client. Connects to the running
  daemon over SignalR for commands that need runtime state. Some commands (config,
  doctor, init) work offline by reading local files directly.

### Why Two Binaries

The daemon loads Akka, ASP.NET, persistence providers, and tool execution
frameworks — heavy dependencies that slow startup. The CLI only needs Termina
(for TUI), a SignalR client, and config file reading. Keeping them separate means
the CLI starts instantly.

### Daemon (`netclawd` / `netclaw daemon start`)

| Component | Description |
|-----------|-------------|
| Akka actor system | Session actors, persistence, scheduling |
| SessionPipeline | Akka.Streams typed input/output channels |
| SignalR hub | `/hub/session` — primary client API |
| Slack adapter | Socket Mode, in-process channel |
| Tool execution | Shell, web fetch, GitHub CLI, MCP |
| Health endpoint | `GET /api/health/ready` |
| Config hot-reload | FileSystemWatcher on `~/.netclaw/` |

The daemon binds `http://127.0.0.1:5199` (loopback only). It can be registered
as a systemd user service (`systemctl --user`, no sudo required) or run in the
foreground for development.

### CLI Client (`netclaw`)

The CLI connects to the daemon via SignalR when needed. If the daemon isn't
running and a command requires it, the CLI prints an error with instructions
(`Daemon not running. Start it with: netclaw daemon start`).

| Category | Commands | Needs Daemon? |
|----------|----------|---------------|
| Daemon management | `daemon start\|stop\|status\|install` | No (manages the daemon itself) |
| Interactive chat | `chat` | Yes (SignalR thin client) |
| Onboarding | `init` | No (reads/writes local config files) |
| Diagnostics | `doctor` | No (reads config, probes services) |
| Configuration | `config show\|validate` | No (reads local files) |
| Personality | `personality reset` | No (resets local files) |
| Sessions | `session list\|inspect\|compact` | Yes (queries daemon state) |
| Tools | `tools list\|policy` | Yes (queries daemon registry) |
| MCP | `mcp list\|validate\|test` | Yes (queries daemon connections) |
| Scheduling | `schedule list\|show\|pause\|resume\|delete` | Yes (queries daemon timers) |
| Memory | `memory show` | Yes (queries daemon memory) |
| Projects | `project list\|add\|remove` | No (reads/writes local files) |
| Environment | `environment scan\|show` | No (scans local system) |
| ACL | `acl validate\|test\|explain` | Yes (tests against running policy engine) |
| Testing | `test smoke` | Yes (end-to-end through daemon) |

Tools execute on the daemon host process. This is the only model that works for
Slack (no client to delegate to), scheduled tasks (autonomous), and Docker
deployment (tools need access to the host's Docker socket). The TUI is a pure
presentation layer — it renders tool call/result output but does not execute
tools.

See `SPEC-011-daemon-architecture.md` for full specification.

## Non-Goals (MVP)

- Ambient channel monitoring with per-channel instructions
- Webhook ingress (Tailscale Serve / Cloudflare Tunnel)
- Sub-agent model routing (cheaper models for high-token tasks)
- Browser automation
- Delegated coding (Claude Code / OpenCode spawning)
- Web management UI implementation (spec + mockups only)
- Formal approval gates and tool isolation/sandboxing
- Telemetry and advanced model capability abstraction layers
- Session branching/revert features

## Primary Personas

- `Owner-Operator`: runs Netclaw on homelab hardware (pi1), interacts through
  Slack (including mobile, on the go), needs predictable behavior, persistence,
  and strong safety defaults.
- `Future Maintainer`: extends capabilities and needs stable behavioral specs.

## Functional Requirements

### FR-001 Slack Thread Session Identity

Session entity ID shall be `{channelId}/{threadTs}` and all interactions for
that thread shall route to the same session actor.

### FR-002 Turn Processing and Broadcast

User input shall produce a persisted turn event and a broadcast event consumed
by the Slack adapter for reply delivery. Pub/sub session broadcasts enable
adapters and future UI subscribers to consume session output without direct
transport coupling.

### FR-003 Persistent Recovery

Session state shall recover from SQLite journal and snapshots after
process restart.

### FR-004 Conversation Compaction

When configured thresholds are exceeded, session history shall compact through
summary reduction and persist a compaction event.

### FR-005 Default-Deny ACL

All inbound interactions and privileged operations shall be denied unless
explicitly allowed by configuration. See PRD-002.

### FR-006 Layered System Prompt

Session context shall be assembled from layered system prompt sources:

1. Core personality (PERSONALITY.md — hardcoded agent identity)
2. Operating instructions (INSTRUCTIONS.md — behavioral guidelines)
3. User context (USER.md — owner preferences)
4. Project overlay (AGENTS.md from registered project — loaded on demand)
5. Session-specific context (conversation, tool results, memory)

Later layers augment or override earlier layers. All soul files are loaded at
session start and cached.

### FR-007 Operator Controls

CLI commands and documented UI contracts shall cover onboarding, config
validation, ACL diagnostics, and session inspection workflows. See PRD-004.

### FR-008 Slack Socket Mode Transport

Slack integration shall use Slack Socket Mode for inbound and outbound message
event handling during MVP, avoiding required public inbound HTTP endpoints.

### FR-009 MCP Tool Integration

Netclaw shall support MCP server integration so tool capabilities can be loaded
from a configured server list with policy enforcement. Memorizer shall serve as
the external memory tier for research, knowledge base, and cross-session
learning. See PRD-006.

### FR-010 Local Memory System

Netclaw shall maintain first-party local memory on disk:

- Agent soul / personality (markdown files)
- Project registry (repo paths, capabilities, AGENTS.md paths)
- Environment inventory (installed tools, credentials, host capabilities)
- Scheduled task definitions
- Channel-level instructions (post-MVP)

Local memory is personal and operational (file paths, tool availability,
project info). Large-corpus knowledge is delegated to Memorizer via MCP.
See PRD-007.

### FR-011 Tool Access

Netclaw shall provide policy-gated access to local tools:

- Web search (Brave Search API or equivalent)
- Web fetch (URL content retrieval)
- Shell execution (sandboxed command execution)
- GitHub (via `gh` CLI)

Tool invocation is subject to ACL grants per PRD-002. Tool results are included
in session context for the LLM.

### FR-012 Chat-Driven Scheduling

The agent shall create, list, and cancel scheduled tasks through conversation.
Scheduled tasks are persisted as JSON and executed by Akka timers. Each
scheduled execution creates a fresh session or runs in a dedicated scheduling
actor. See PRD-008.

### FR-013 Capability Self-Discovery

Netclaw shall maintain awareness of its environment:

- Is `claude` / `opencode` CLI available?
- Do I have git credentials? For which hosts?
- What .NET SDK is installed?
- What repos are registered and where on disk?
- What MCP servers are configured and reachable?

Discovery runs at startup and can be re-triggered through conversation.
See PRD-007.

### FR-014 Self-Configuration

The agent shall modify its own configuration files through conversation:

- Update personality, instructions, and user preferences
- Register and unregister projects
- Update environment inventory
- Create and manage scheduled tasks

Configuration is cached in LLM context at session start. Session reboot
refreshes the context. See PRD-007.

### FR-015 Pre-Compaction Memory Flush

Before context compaction occurs, Netclaw shall trigger a silent agentic turn
prompting the model to write durable memories to disk. This directly counters
context rot — losing important information when context resets. See PRD-007.

### FR-016 Config Change Restart Coordination

Netclaw shall monitor operational configuration files for changes and validate
them before they affect runtime behavior.

- **Watched files**: operational daemon config written to `netclaw.json`
- **Session-scoped files** (require session reboot or fresh turn context):
  personality files, project registry, environment inventory

Mechanism: `FileSystemWatcher` + 500ms debounce + validate-before-restart.
Invalid config changes SHALL be rejected with logged diagnostics and SHALL keep
the current daemon instance running. Valid changes SHALL trigger coordinated
daemon restart: close new ingress, drain active sessions, restart, relaunch the
sessions that were active, and resume from the last durable checkpoint.

## Operational Requirements

- Daemon deploys as a user-level systemd service on `pi1` (no sudo required)
- CLI binary is a separate lightweight executable
- No required public inbound HTTP path for base Slack operation
- Secure failure mode: invalid policy/config blocks startup
- CI/CD test path does not require live model provider credentials
- Agent data directory (`~/.netclaw/` or configured path) stores all local
  memory, config, and schedule files

## Acceptance Tests

1. Allowed user posts in Slack thread -> Netclaw replies in thread.
2. Restart host -> same thread follow-up reflects prior context.
3. Long thread triggers compaction without losing active task objective.
4. Disallowed sender/channel is rejected and logged as policy deny.
5. CLI config validation reports pass before runtime start.
6. Agent personality is consistent across sessions and restarts.
7. Agent can list registered projects and environment capabilities.
8. Agent can create a scheduled task through conversation.
9. Agent can use web search and shell tools when policy allows.
10. MCP Memorizer stores and retrieves cross-session knowledge.

## Phasing

1. **Chat + Memory MVP** (this PRD) — Slack, persistence, compaction, local
   memory, MCP/Memorizer, basic tools, scheduling
2. **Input Expansion** — ambient channels, webhooks, channel instructions,
   onboarding wizard
3. **Delegated Coding** — Claude Code / OpenCode spawning, process monitoring
4. **Browser + Research** — web automation, price monitoring, research pipelines
5. **Ops Console** — web UI for config, sessions, diagnostics

## Risks and Mitigations

- `Risk`: accidental security drift while adding convenience features.
  - `Mitigation`: PRD + OpenSpec traceability and default-deny tests.
- `Risk`: persistence model lock-in to unstable message types.
  - `Mitigation`: framework-owned serializable message envelope only.
- `Risk`: MVP scope creep toward north-star architecture.
  - `Mitigation`: explicit non-goals and change reviews against this PRD.
- `Risk`: context rot from long-running sessions losing important memories.
  - `Mitigation`: pre-compaction memory flush pattern (FR-015).
- `Risk`: agent self-modification introduces inconsistent state.
  - `Mitigation`: session reboot on config change; validate before write.

## Cross-References

- Security: PRD-002
- Ops Console: PRD-003 (deferred to Phase 5)
- CLI Onboarding: PRD-004
- Model Providers: PRD-005
- MCP Integration: PRD-006
- Agent Personality and Memory: PRD-007
- Scheduling: PRD-008
- Input Adapters: PRD-009 (post-MVP)
- Daemon Architecture: SPEC-011
- Architecture Research: `docs/research/agent-gateway-architecture.md`
