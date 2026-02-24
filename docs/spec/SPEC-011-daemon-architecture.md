# SPEC-011: Daemon Architecture and Process Model

Source PRDs: `PRD-001`, `PRD-002`, `PRD-004`

## Purpose

Define the single-process daemon model, mode selection, gateway surface,
configuration hot-reload, and deployment model for Netclaw.

This spec complements:
- `SPEC-001` (runtime boundaries — logical separation within the process)
- `SPEC-004` (CLI contract — command surface)
- `SPEC-006` (gateway exposure — network access controls)
- `SPEC-007` (guided onboarding — init wizard flow)

Research basis: `docs/research/agent-gateway-architecture.md` — analysis of
OpenClaw, IronClaw, and ZeroClaw validates single-process architecture for
homelab/personal agent use.

## Single-Process Model

Netclaw runs as a single OS process. All components — Akka actor system,
persistence, gateway endpoints, TUI, and tool execution — share one process
boundary. This matches the consensus architecture from OpenClaw, IronClaw, and
ZeroClaw.

The CLI/TUI is "just another channel" that uses the same `SessionPipeline`
abstraction as any other channel adapter (Slack, scheduled tasks, webhooks).

### Logical Boundaries (within one process)

```
┌────────────────────────────────────────────────────────────┐
│                     Netclaw Process                        │
│                                                            │
│  ┌──────────────────┐  ┌────────────────────────────────┐  │
│  │   Presentation   │  │          Gateway               │  │
│  │                  │  │                                │  │
│  │  Termina TUI     │  │  SignalR Hub (/hub/session)   │  │
│  │  Headless Client │  │  Health Probe (/api/health)   │  │
│  └────────┬─────────┘  └──────────────┬─────────────────┘  │
│           │                           │                    │
│           ▼                           ▼                    │
│  ┌─────────────────────────────────────────────────────┐   │
│  │              SessionPipeline                        │   │
│  │   (Akka.Streams — typed Input/Output channels)      │   │
│  └────────────────────────┬────────────────────────────┘   │
│                           │                                │
│  ┌────────────────────────▼────────────────────────────┐   │
│  │           Akka Actor System                         │   │
│  │   SessionManager → LlmSessionActor (per session)   │   │
│  │   Persistence (journal + snapshots)                 │   │
│  │   Tool Execution (shell, file, MCP)                 │   │
│  └─────────────────────────────────────────────────────┘   │
└────────────────────────────────────────────────────────────┘
```

In-process channels (TUI, headless) call `SessionPipeline.CreateAsync()`
directly — no network hop. The SignalR hub uses the same `SessionPipeline`
for remote clients.

## Mode Selection

The executable supports multiple modes selected by command-line arguments.
Modes fall into two categories based on service requirements.

### Daemon Modes (full service stack)

These modes boot the complete service stack: Akka actor system, persistence,
`SessionPipeline`, tool registry, SignalR hub, and health endpoint.

| Command | Behavior |
|---------|----------|
| `netclaw run` | Daemon only. Hosts actors, gateway, Slack adapter, scheduler. Runs as a service. |
| `netclaw chat` | Daemon + Termina TUI. Interactive chat via `SessionPipeline`. |
| `netclaw -p "prompt"` | Daemon + headless client. Single turn via `SessionPipeline`, exits on `TurnCompleted`. |

### Lightweight Modes (config services only)

These modes boot a minimal host with configuration services only. No Akka
actor system, no persistence, no SignalR, no tool execution.

| Command | Behavior |
|---------|----------|
| `netclaw init` | Reentrant TUI wizard for provider/model/Slack/MCP configuration. |
| `netclaw doctor` | Health checks against config files and provider connectivity. |

### Service Registration Split

Shared services (all modes):
- `NetclawPaths` — directory layout
- `IConfiguration` chain — netclaw.json + secrets.json + NETCLAW_* env vars
- `ChatClientFactory` — creates `IChatClient` from provider config
- `IChatClientProvider` — resolves clients by model role
- `TimeProvider` — virtualized time

Daemon-only services:
- Akka actor system (with `WithNetclawActors()`)
- Persistence (journal + snapshot store)
- `SessionPipeline` — stream factory for channels
- `ToolRegistry` + `IToolExecutor` — tool execution
- `ISystemPromptProvider` — layered system prompt assembly
- `ConfigWatcherService` — file system hot-reload
- SignalR hub

### Host Selection

Lightweight modes use `Host.CreateApplicationBuilder()` (standard .NET host).
Daemon modes use `WebApplication.CreateBuilder()` (ASP.NET host for SignalR
and health endpoints).

## Gateway Surface

### Phase 1 (MVP)

Minimal external surface. In-process channels are the primary interaction model.

**SignalR Hub** (`/hub/session`):
Mapped and documented for future remote clients (Blazor ops console, remote
CLI). Not actively used by the TUI or headless modes in Phase 1.

Contract:
```
Client → Server:
  CreateSession(channelType: string) → sessionId: string
  SendMessage(sessionId: string, text: string) → void

Server → Client:
  ReceiveOutput(output: SessionOutputDto) → void
```

`SessionOutputDto` is a wire-safe mapping of `SessionOutput` (the actor
protocol type). The mapper handles discriminated union → flat DTO conversion.

**Health Probe** (`GET /api/health/ready`):
Returns `200 OK` when the host is accepting connections. Used for Docker
health checks and external monitoring.

### Future Phases

REST endpoints for schedule CRUD, project management, tool listing, etc. will
be added when remote clients (Blazor ops console) require them. The ASP.NET
pipeline is already in place — no architectural debt.

## Tool Execution Model

Tools execute on the host process. The daemon runs shell commands, file
operations, Docker commands, and MCP tool calls. This is the only model that
works for:

- **Slack channel**: No client process to delegate tool execution to.
- **Scheduled tasks**: Execute autonomously without any connected client.
- **Docker deployment**: Tools need access to the host's Docker socket.

The TUI is a presentation layer — it renders tool call/result output but does
not execute tools.

## Configuration Hot-Reload

### Trigger

`FileSystemWatcher` on `~/.netclaw/config/` monitors for changes to
`netclaw.json` and `secrets.json`.

### Behavior

1. File change event received
2. 500ms debounce timer started (reset on additional events)
3. After debounce: read and validate new configuration
4. **Valid config**: rebuild `IChatClientProvider`, notify actor system
5. **Invalid config**: log warning with validation errors, preserve previous config

### What Reloads

- Provider credentials and endpoints
- Model selections (main, fallback, compaction)
- Session parameters (compaction threshold, tool iteration limits)
- Tool configuration (shell timeout, output limits)

### What Does Not Reload (requires restart)

- Akka actor system configuration
- Persistence provider
- Network binding / exposure mode

### Sources of Config Changes

- `netclaw init` TUI wizard (writes incrementally per section)
- Manual file editing by operator
- Agent self-configuration via `config_write` tool grant (SEC-008)

All changes go to disk first. The `FileSystemWatcher` is the single reload
trigger — there is no in-memory config mutation path.

## Reentrant Init Wizard

`netclaw init` is designed for both first-run and reconfiguration.

### First Run

Linear guided flow through all sections (per SPEC-007). Config written
incrementally as each section completes.

### Subsequent Runs

Dashboard view showing current configuration state per section. Each section
shows status (configured/unconfigured/error). Operator can jump to any section
to modify or add entries.

### Sections

| Section | Purpose |
|---------|---------|
| Providers | Add/modify LLM provider endpoints and credentials |
| Models | Assign provider + model to each role (main, fallback, compaction) |
| Slack | Bot token, app token, Socket Mode configuration |
| Persistence | PostgreSQL connection string (future, in-memory for MVP) |
| MCP | Add/modify MCP server connections |
| Exposure | Choose network exposure mode (local/tailscale/cloudflare) |
| Health Check | Run validation across all configured services |

### Provider Onboarding Flow (within Providers section)

1. Choose provider type (Ollama, OpenRouter, future: Anthropic, OpenAI)
2. Enter endpoint URL
3. Enter API key (if required, masked input)
4. Test connectivity (direct HTTP to provider endpoint)
5. List available models from provider
6. Select default model
7. Write to `netclaw.json` / `secrets.json`

This is a local operation — the wizard calls `ChatClientFactory` and provider
APIs directly through DI. No daemon or REST endpoint required.

## Security Context

Every connection (SignalR, in-process channel) carries a `ChannelSecurityContext`
that identifies the trust level.

| Level | Description | Phase |
|-------|-------------|-------|
| `LocalOperator` | Local connection, full trust | Phase 1 (MVP) |
| `Authenticated` | Validated remote sender, ACL-gated | Future |
| `Anonymous` | Default deny | Future |

In Phase 1, all connections are `LocalOperator` and the gateway binds
loopback-only (SEC-005).

## Docker Deployment Model

Recommended production deployment:

```bash
docker run -d \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v ~/.netclaw:/root/.netclaw \
  -p 127.0.0.1:5000:5000 \
  netclaw run
```

- Docker socket mount enables host container management
- Config volume persists across container restarts
- Port binding on loopback only (SEC-005 default)
- Container includes Docker CLI, git, and other management tools

Tools executed by the agent (shell commands, Docker operations) run inside the
container and reach the host Docker daemon via the mounted socket. This is the
same pattern used by Portainer, Watchtower, and Dockge.

## Cross-References

- Runtime boundaries: SPEC-001
- Session lifecycle: SPEC-002
- Security controls: SPEC-003
- CLI contract: SPEC-004
- Operator UI: SPEC-005
- Gateway exposure: SPEC-006
- Guided onboarding: SPEC-007
- Architecture research: `docs/research/agent-gateway-architecture.md`
