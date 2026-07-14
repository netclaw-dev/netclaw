# SPEC-011: Daemon Architecture and Process Model

Source PRDs: `PRD-001`, `PRD-002`, `PRD-004`

## Purpose

Define the daemon + thin client architecture, binary split, SignalR transport,
daemon lifecycle management, command routing, and deployment model for Netclaw.

This spec complements:
- `SPEC-001` (runtime boundaries — logical separation within the daemon)
- `SPEC-004` (CLI contract — command surface)
- `SPEC-006` (gateway exposure — network access controls)
- `SPEC-007` (guided onboarding — init wizard flow)

Architecture follows the pattern established by OpenClaw, IronClaw, and PicoClaw:
a persistent daemon process with thin CLI/TUI clients connecting over a local
transport.

## Two-Binary Architecture

Netclaw ships as two binaries with distinct dependency profiles:

### `Netclaw.Daemon`

Always-on background service. Owns all agent logic, persistence, tool execution,
and channel management.

```
Netclaw.Daemon Process
├── ASP.NET Host (WebApplication)
│   ├── SignalR Hub (/hub/session)     ← primary client API
│   ├── Readiness Probe (/api/health/ready)
│   └── Runtime Status JSON (/api/health/status)
│
├── SessionPipeline (Akka.Streams — typed Input/Output channels)
│
├── Akka Actor System
│   ├── SessionManager → LlmSessionActor (per session)
│   ├── ScheduleManagerActor (scheduled tasks)
│   ├── Persistence (journal + snapshots)
│   └── ToolRegistry + IToolExecutor
│
├── Channel Adapters (in-process)
│   ├── Slack Socket Mode
│   └── Internal Timer
│
├── ConfigWatcherService + RestartCoordinator (FileSystemWatcher restart coordination)
└── ISystemPromptProvider (layered soul files)
```

SDK: `Microsoft.NET.Sdk.Web`
Key dependencies: Akka.NET, Akka.Persistence, ASP.NET Core (SignalR server),
Netclaw.Actors, Netclaw.Configuration, OllamaSharp / OpenAI client

Binds: address and port from `DaemonConfig` (`Host`, `Port`); defaults to `http://127.0.0.1:5199` (loopback only). `ExposureMode` declares network reachability and tunnel infrastructure, separately from chat audience/profile selection.

### `Netclaw.Cli`

Lightweight CLI and TUI client. No actor system, no persistence, no tool
execution.

```
Netclaw.Cli Process
├── Command Router (args[0] dispatch)
│
├── TUI Commands (Termina)
│   ├── ChatPage → SignalR client → daemon
│   └── InitWizardPage → local file I/O
│
├── Daemon Management
│   ├── start/stop/status (process control)
│   └── install/uninstall (systemd user service)
│
├── Plain CLI Commands
│   ├── Offline: config, doctor, project, environment, personality
│   └── Daemon-required: session, tools, mcp, schedule, memory, acl, test
│
└── SignalR Client (Microsoft.AspNetCore.SignalR.Client)
    └── Connects to daemon at http://127.0.0.1:5199/hub/session
```

SDK: `Microsoft.NET.Sdk`
Key dependencies: Termina, Microsoft.AspNetCore.SignalR.Client,
Netclaw.Actors (protocol types only), Netclaw.Configuration

### Shared Libraries

Both binaries reference these class libraries:

- **`Netclaw.Actors`** — protocol types (`SessionOutput`, `ChannelInput`,
  `SessionId`), actor interfaces. The CLI only uses the protocol types for
  SignalR serialization — it does not host actors.
- **`Netclaw.Configuration`** — `SessionConfig`, `ProviderEntry`,
  `ModelSelection`, `NetclawPaths`. Used by the CLI for config file operations
  and by the daemon for runtime configuration.

### Tool Execution Scope

The daemon admits each tool batch with an immutable run scope containing its
session binding, audience and trust boundary, delivery metadata, project and
working-directory context, model modalities, and output budget. Tool
implementations receive a required invocation view of that scope; there is no
context-free production dispatch path.

Each invocation owns its output collection and the pipeline owns its mutable
approval-attempt state. Parallel calls may share the immutable admitted scope,
but never mutable output or approval state. MCP remains the executable's only
extension boundary and receives the same required invocation context without
changing its request or response schema.

## SignalR Hub Contract

The SignalR hub at `/hub/session` is the primary API between clients and the
daemon. Both the TUI and daemon-required CLI commands use this transport.

### Client → Server (Hub Methods)

```
CreateSession(channelType: string) → sessionId: string
SendMessage(sessionId: string, text: string) → void
CompactSession(sessionId: string) → void
ListSessions() → SessionSummary[]
GetSessionState(sessionId: string) → SessionStateDto
ListTools() → ToolSummary[]
ListSchedules() → ScheduleSummary[]
// Additional methods per CLI command requirements
```

### Server → Client (Callbacks)

```
ReceiveOutput(output: SessionOutputDto) → void
```

`SessionOutputDto` is a wire-safe mapping of the `SessionOutput` discriminated
union. The mapper handles union → flat DTO conversion for SignalR serialization.

### Connection Lifecycle

1. Client connects to `http://127.0.0.1:5199/hub/session`
2. For chat: client calls `CreateSession("tui")` → receives session ID
3. Client subscribes to output via `ReceiveOutput` callback
4. Client sends messages via `SendMessage(sessionId, text)`
5. Daemon streams `SessionOutput` events back to client
6. Client disconnects on exit — daemon keeps session alive for reconnection

## Command Routing

Commands fall into three categories based on their runtime requirements:

### Daemon Management (no daemon required)

These commands manage the daemon process itself. They work whether or not the
daemon is running.

| Command | Behavior |
|---------|----------|
| `netclaw daemon start` | Start daemon as background process |
| `netclaw daemon stop` | Stop running daemon gracefully |
| `netclaw daemon status` | Report running/stopped, PID, uptime |
| `netclaw daemon install` | Register systemd user service |
| `netclaw daemon uninstall` | Remove service registration |

### Offline Commands (daemon optional)

These commands read/write local files in `~/.netclaw/` and scan the local
system. They do not require the daemon to be running, but some commands may use
daemon state opportunistically when it is available.

| Command | Behavior |
|---------|----------|
| `netclaw init` | TUI onboarding wizard (config file I/O) |
| `netclaw doctor` | Validate config and prefer daemon-backed MCP auth/connectivity status when available |
| `netclaw config show` | Dump merged config to stdout |
| `netclaw config validate` | Check config for errors |
| `netclaw project list\|add\|remove` | Manage project registry (local files) |
| `netclaw environment scan\|show` | Discover local system capabilities |
| `netclaw personality reset` | Reset soul file to default |

### Daemon-Required Commands

These commands connect to the daemon over SignalR. If the daemon isn't running,
the CLI prints an error and exits with code 1:

```
Error: Daemon not running.
Start it with: netclaw daemon start
```

| Command | Behavior |
|---------|----------|
| `netclaw chat` | TUI thin client (streaming SignalR session) |
| `netclaw session list\|inspect\|compact` | Query session state |
| `netclaw tools list\|policy` | Query tool registry |
| `netclaw mcp list\|validate\|test` | Query MCP connections |
| `netclaw schedule list\|show\|pause\|resume\|delete` | Manage scheduled tasks |
| `netclaw memory show` | Query agent memory |
| `netclaw acl validate\|test\|explain` | Test against running policy engine |
| `netclaw test smoke` | End-to-end smoke test |

## Daemon Lifecycle Management

### Process Control

`netclaw daemon start` spawns the daemon as a detached background process.
The daemon writes its PID to `~/.netclaw/netclaw.pid` for lifecycle management.

`netclaw daemon stop` reads the PID file and sends SIGTERM for graceful
shutdown. The daemon handles SIGTERM by draining active sessions and stopping
the actor system cleanly.

`netclaw daemon status` checks the PID file and verifies the process is alive.
Reports: running/stopped, PID, uptime, port, number of active sessions.

`netclaw update` preserves the daemon's lifecycle owner. If the Linux systemd
user unit is active or enabled, update stops and starts `netclaw.service` via
`systemctl --user` instead of spawning a detached daemon. If no systemd user
unit owns the daemon, update uses the direct detached process lifecycle.

### Crash interception and evidence

The daemon installs process-level exception handlers at startup for:

- `AppDomain.CurrentDomain.UnhandledException`
- `TaskScheduler.UnobservedTaskException`

On either path, Netclaw writes a crash log under `~/.netclaw/logs/crash-*.log`
with process diagnostics and the latest known session/turn context. When DI is
available, the daemon also emits an operational alert with type
`daemon.crashing` (category `DaemonCrashed`) so configured webhook targets can
capture crash signals even when the process is unstable.

### Service Registration (Linux)

`netclaw daemon install` creates a systemd user service at
`~/.config/systemd/user/netclaw.service`:

```ini
[Unit]
Description=Netclaw Daemon
After=network.target

[Service]
Type=simple
ExecStart=/path/to/netclawd
ExecStop=/path/to/netclaw daemon stop
Restart=always
RestartSec=5
Environment=DOTNET_ENVIRONMENT=Production
EnvironmentFile=-/home/you/.netclaw/config/daemon.env

[Install]
WantedBy=default.target
```

No sudo required. Uses `systemctl --user enable netclaw` and
`loginctl enable-linger $USER` to survive user logout.

**Shell-tool PATH.** A systemd `--user` service starts with a sanitized,
non-interactive environment that does not inherit the operator's login-shell
`PATH`, so the agent's shell tool cannot resolve `netclaw`, `dotnet`, or
`~/.local/bin` binaries. Rather than bake a guessed directory list into the unit
(which can never anticipate every environment — see issue #1544), `install`
**captures the operator's real `PATH` from its own process** (the CLI is a child
of the operator's shell, so no shell is spawned and no dotfiles are sourced) and
writes `PATH=<installDir>:<captured>:<system floor>` (de-duplicated, empty elements dropped; the
floor `/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin` keeps the shell functional even if the
captured PATH was empty or partial) to `~/.netclaw/config/daemon.env`. The unit
loads it via `EnvironmentFile=-…` (the `-` makes a missing file degrade tool
resolution rather than block startup). `netclaw doctor --fix` rehydrates the file
from the operator's current `PATH`; `SystemdUnitPathDoctorCheck` validates the
wiring and contents. Producer, rehydrator, and validator share
`DaemonPathEnvironmentFile`. The value is a snapshot as of the last
`install`/`doctor --fix`, so after installing new tools re-run either and then
`systemctl --user restart netclaw`.

`netclaw daemon uninstall` stops the service and removes the unit file and the
`daemon.env` file.

### Service Registration (macOS)

`netclaw daemon install` creates a LaunchAgent at
`~/Library/LaunchAgents/com.stannardlabs.netclaw.plist` with `KeepAlive=true`
and `RunAtLoad=true`.

## TUI Chat Client

`netclaw chat` is a pure thin client. It contains no agent logic, tool
execution, or persistence. The architecture:

```
ChatPage (Termina rendering)
    ↕ binds to
ChatViewModel (reactive state)
    ↕ uses
SignalR Client Adapter
    ↕ connects to
Daemon SignalR Hub (/hub/session)
```

The `ChatViewModel` interface remains the same as the current in-process
implementation — it exposes `IObservable<SessionOutput>` and accepts
`SubmitAsync(text)`. The only change is the backend: SignalR client instead of
direct `SessionPipeline`.

`ChatPage` does not change at all. Same rendering, same paste debounce, same
status bar, same tool call spinners.

## Tool Execution Model

Tools execute on the daemon host process. The daemon runs shell commands, file
operations, Docker commands, and MCP tool calls. This is the only model that
works for:

- **Slack channel**: No client process to delegate tool execution to.
- **Scheduled tasks**: Execute autonomously without any connected client.
- **Docker deployment**: Tools need access to the host's Docker socket.

The TUI is a presentation layer — it renders tool call/result output but does
not execute tools.

## Configuration Restart Coordination

### Trigger

`FileSystemWatcher` on `~/.netclaw/config/` monitors for changes to
`netclaw.json`.

### Behavior

1. File change event received
2. 500ms debounce timer started (reset on additional events)
3. After debounce: read and validate new configuration
4. **Valid config**: close daemon-managed ingress, enumerate live session actors,
   ask them to drain, persist a restart manifest, and request coordinated
   daemon restart
5. **After restart**: warm the sessions that were active when restart began and
   inject a continuity notice for the next turn
6. **Invalid config**: log warning with validation errors, preserve previous config

### What Changes Take Effect After Restart

- Provider credentials and endpoints
- Model selections (main, fallback, compaction)
- Session parameters (compaction threshold, tool iteration limits)
- Tool configuration (shell timeout, output limits)

### What Still Requires Manual Session Resume Or Reconnect

- Live transport connections, SignalR sockets, and Slack delivery handles
- New inbound work that arrives after restart drain begins

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

## Security Context

Every SignalR connection carries a `ChannelSecurityContext` that identifies the
trust level.

| Level | Description | Phase |
|-------|-------------|-------|
| `LocalOperator` | Loopback connection, full trust | Phase 1 (MVP) |
| `Authenticated` | Validated remote sender, ACL-gated | Future |
| `Anonymous` | Default deny | Future |

In Phase 1, all connections are `LocalOperator` and the daemon binds
loopback-only (SEC-005).

## Distribution and Installation

Netclaw ships as a single tarball containing both binaries. The user adds
`netclaw` to their `PATH` — the CLI binary handles everything including
launching the daemon.

### Package Layout

```
netclaw-{version}-{os}-{arch}.tar.gz
├── netclaw          # CLI binary (goes on PATH)
├── netclawd         # Daemon binary (managed by CLI)
└── README.md        # Quick start
```

### Installation

```bash
# Extract and add to PATH
tar xzf netclaw-*.tar.gz -C ~/.local/share/netclaw/
ln -s ~/.local/share/netclaw/netclaw ~/.local/bin/netclaw

# Or system-wide
sudo tar xzf netclaw-*.tar.gz -C /usr/local/lib/netclaw/
sudo ln -s /usr/local/lib/netclaw/netclaw /usr/local/bin/netclaw
```

### Binary Discovery

The CLI locates the daemon binary by checking (in order):
1. Same directory as the CLI binary (`Path.GetDirectoryName(Assembly.Location)`)
2. `NETCLAW_DAEMON_PATH` environment variable (override)

When `netclaw daemon start` is invoked, the CLI spawns `netclawd` from the
discovered path as a detached background process.

### .NET Tool Distribution (future)

For .NET developers, Netclaw can also be distributed as a .NET global tool:
```bash
dotnet tool install --global netclaw
```
This installs both binaries via NuGet package. The `netclaw` tool shim handles
PATH automatically.

## Docker Deployment Model

Recommended production deployment:

```bash
docker run -d \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v ~/.netclaw:/home/netclaw/.netclaw \
  -p 127.0.0.1:5199:5199 \
  netclaw-daemon
```

- Docker socket mount enables host container management
- Config volume persists across container restarts. The image entrypoint repairs
  writable bind-mount ownership to the dedicated `netclaw` runtime UID before
  starting the daemon.
- Port binding on loopback only (SEC-005 default)
- Container includes Docker CLI, git, and other management tools
- Only the daemon runs in Docker — the CLI runs on the host and connects
  to the daemon's exposed SignalR port

## Cross-References

- Runtime boundaries: SPEC-001
- Session lifecycle: SPEC-002
- Security controls: SPEC-003
- CLI contract: SPEC-004
- Operator UI: SPEC-005
- Gateway exposure: SPEC-006
- Guided onboarding: SPEC-007
