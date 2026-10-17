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

At startup, `McpClientManager` starts each enabled server connection concurrently.
It waits for every initial attempt before the daemon listener starts. Each
server uses its own connection gate. The shared registries publish complete
catalogs before the listener accepts a session. A failed server keeps its
error status while other server catalogs remain available.

### `Netclaw.Cli`

The CLI owns one local client actor for daemon sessions.
The daemon retains persistence, model calls, and tool execution.
Offline bootstrap creates no client ActorSystem.
The client creates its ActorSystem lazily on its first daemon command.

```
Netclaw.Cli Process
├── Command Router (args[0] dispatch)
│
├── TUI Commands (Termina)
│   ├── ChatPage → local client actor → SignalR → daemon
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
  SignalR serialization. It also hosts the local client control actor.
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

`EnsureSession` returns `SessionEnsureResultDto` with `TextAdmissionVersion = 1`.
The client checks that version before text dispatch and after each reattachment.
A missing field means unsupported version 0. Unknown versions fail explicitly.
The positional DTO constructor and existing hub signatures remain compatible.

`SendMessage` succeeds after the session journal stores the text admission record.
The registry retains the connection, target attachment, identity, and ingress checks.
The canonical pipeline carries `AckTarget` to the session actor.
A queue write alone does not confirm admission.
A journal rejection returns an error with the `Text rejected: ` prefix.
A lost or invalid acknowledgement leaves admission unconfirmed.
Other `CommandAck` paths retain their existing meanings.

### Server → Client (Callbacks)

```
ReceiveOutput(output: SessionOutputDto) → void
```

`SessionOutputDto` is a wire-safe mapping of the `SessionOutput` discriminated
union. The mapper handles union → flat DTO conversion for SignalR serialization.

### Connection Lifecycle

1. Client connects to `http://127.0.0.1:5199/hub/session`
2. A fresh chat calls `EnsureSession` on its first input. A resume chat attaches when its page opens.
3. Client subscribes to output via `ReceiveOutput` callback
4. Client sends messages via `SendMessage(sessionId, text)`
5. Daemon streams `SessionOutput` events back to client
6. Normal quit permits two seconds total for prior admission. It does not wait for a model response.
7. The client prints unresolved delivery status after the terminal UI closes.
8. Client disconnect removes its subscriber. The daemon retains admitted work.

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
When an installed Linux systemd user unit owns the daemon, `start` runs
`systemctl --user start netclaw.service` instead, and it reports "Daemon already
running" without touching the unit when a detached daemon already holds the home
(the unit's daemon would only loop on the singleton lock).

`netclaw daemon stop` guarantees that, once it succeeds, nothing brings this
home's daemon back. It stops the installed systemd user unit when the unit could
(re)start a daemon for this home (`active`, `activating` including auto-restart,
or `reloading`), then stops any daemon process still alive (a detached copy
beside the unit, for example). A unit that is `inactive`, `failed` or
`deactivating` is left alone: `deactivating` is the unit's own `ExecStop` calling
back in. Otherwise `stop` reads the PID file and sends SIGTERM for graceful
shutdown. The daemon handles SIGTERM by draining active sessions and stopping
the actor system cleanly. Under the container supervisor
(`NETCLAW_CONTAINER_SUPERVISOR`) the stop still happens, exits 0, and prints that
the supervisor will restart the daemon; this is how a containerised daemon is
bounced from the CLI.

The unit serves a home when it has a main process and that process's own
`NETCLAW_HOME` (read from `/proc/<MainPID>/environ`, so it sees `Environment=` and
`EnvironmentFile=` alike; absent means the default home) is that home, links
resolved. If the environment cannot be read, a `MainPID` equal to the home's
daemon PID counts. When the unit has no main process (the crash-loop window), the
unit's `Environment` property decides: no `NETCLAW_HOME` means the default home.
A unit whose `NETCLAW_HOME` comes only from an `EnvironmentFile`, sampled at an
instant when it has no main process, is therefore treated as serving the default
home. For a home the unit does not serve, `start` and `stop` act on that home's
own daemon process and never on the unit. A stale PID file that names another
home's live `netclawd` is still trusted by a direct `stop`, as in 0.27.1.

The session journals each accepted input before it acknowledges the source.
The record retains the text, media, source message ID, and original authority.
A completed reply, a started tool batch, or a terminal failure consumes the
input ID. The actor restores unconsumed records from the journal after a cold
start. A retry with the same stable source message ID does not add a second
record. A source without a stable ID cannot use this deduplication rule.

During drain, a session can stop a tool task that waits only for durable
approval prompts. The session waits for the tool task to stop before it
acknowledges drain. Its journal retains the prompts and completed sibling
results. An approval after restart resumes the original turn under its
recorded authority. Active tools, accepted buffered input, and incomplete
results keep the current bounded drain path.

The pipeline reports cancellation through its canceled task state. The daemon
keeps the existing approval prompt. Button and text responses can resume the
recovered turn. A channel UI can temporarily lag the session state after restart.

During any graceful stop, the actor gives an active model call a two-second
completion grace. It then cancels an eligible call and waits for its task.
The actor returns a standard `current_session` reminder for durable pending
input. The reminder expires ten minutes after the interruption. Startup
registers each fresh reminder through the reminder manager. The session
restores the pending input under its recorded authority when the reminder
arrives. A completed turn, partial text, or possible tool effect produces no
restart reminder.

The daemon gives session drain 20 seconds within a 30-second shutdown phase.
The CLI allows 45 seconds before forced termination. The generated systemd
unit allows 60 seconds. A container should set
`terminationGracePeriodSeconds` to at least 60 seconds.

`netclaw daemon status` checks the PID file and verifies the process is alive,
then probes `/api/health/ready` on this home's own daemon endpoint (the `Daemon`
section of `netclaw.json`, not `NETCLAW_DAEMON_ENDPOINT` or a paired remote).
It exits 1 when the process is not running or readiness does not answer, which
the container `HEALTHCHECK` relies on. Reports: running/stopped, PID, uptime.

`netclaw update` preserves the daemon's lifecycle owner, using the same stop and
start path as `netclaw daemon stop` and `start`. If the Linux systemd user unit
is active or enabled (and the home is the default one), update stops and starts
`netclaw.service` via `systemctl --user` instead of spawning a detached daemon.
If no systemd user unit owns the daemon, update uses the direct detached process
lifecycle.

### Crash interception and evidence

The daemon installs process-level exception handlers at startup for:

- `AppDomain.CurrentDomain.UnhandledException`
- `TaskScheduler.UnobservedTaskException`

On either path, Netclaw writes a crash log under `<NETCLAW_HOME>/logs/crash-*.log`;
`NETCLAW_HOME` defaults to `~/.netclaw`
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

**Operator lines.** `daemon.env` is also the place for operator settings, for example
`NETCLAW_HOME=/x/home` or `HTTP_PROXY=...`. Install and `doctor --fix` replace the first
`PATH=` line in place, drop a later `PATH=` line, and add `PATH=` at the end when none exists.
They keep every other line verbatim and in order. They compare only the text before the first
`=` and never read another line. A rerun with the same input leaves the file byte-identical.
The file mode is 0600 because the values are plain text, unlike the encrypted values in
`secrets.json`.

`netclaw daemon install --env KEY=VALUE` adds or replaces a line. The flag is repeatable. A key
must match `NETCLAW_[A-Za-z0-9_]+`, a value must stay on one line, and the same key may appear
once. A value must not hold `\`, `'`, or `"`, and it must not start or end with a space. systemd
reads these characters in an `EnvironmentFile=` as a line continuation, a quote, an escape, or a
trim, so the daemon would see another value. Install refuses the value and does not change it.
The embedded config schema is the allow-list for the key. Install accepts `NETCLAW_<path>` only when
`<path>` names a property that `netclaw-config.v1.schema.json` declares. The match ignores case. The
name of an entry in a keyed map (a provider, a model definition, an MCP server, a channel id)
matches as a wildcard, and the property after it must be declared. `NETCLAW_Providers__main__Type`
passes. `NETCLAW_Providers__main__Typo` fails. Two more shapes pass. A keyed map whose entry is an array
passes with an index: `NETCLAW_Tools__AudienceProfiles__Team__McpServerToolGrants__github__0=read_file`.
A log category passes when its value is a log level: `NETCLAW_Logging__LogLevel__Akka=Debug`. An open
section whose declared properties are all the same list of choices takes that list for a name that the
schema does not declare, and the value must be one of the choices (the match ignores case). A key under a
free-form section (`EnvironmentVariables`, `Headers`, `VendorOptions`, or a map of free text) fails, because
the schema gives no meaning to a name there and netclaw cannot tell a credential from a setting.
`NETCLAW_McpServers__db__EnvironmentVariables__SMTP_PASS` fails for that reason. A key at the top level of
netclaw.json that the schema does not declare fails as an undeclared key. A key that names a section or a
list and not a property fails. A name in a key holds only letters, digits, and `_`, so a provider that
`--env` sets needs a name without `-`. `DaemonEnvKeyPolicy` walks the schema with the same
`SchemaFixResolver.StepInto` step that the doctor fix uses. `NETCLAW_HOME` and
`NETCLAW_DAEMON_ENDPOINT` are process settings that no schema describes, and both pass. A declared
property that holds a credential fails: `SchemaFixResolver.HoldsCredential` decides by the property name,
for example `ApiKey`, `OAuthRefreshToken`, or `OAuthClientSecret`. That rule skips a property that cannot
hold text, so `Session__FirstTokenTimeoutSeconds` passes. A URL with user info fails under any key. Each
refusal names the key and says why. It points to `netclaw secrets set` for a credential and to
`netclaw.json` for any other setting. No message holds a value. The check works on key paths and URL
shapes. It does not prove that a value holds no secret. Install checks every entry before it runs `systemctl` or
writes a file. `daemon install` refuses an argument other than `--env KEY=VALUE`.

If `daemon.env` is a symbolic link, install and `doctor --fix` write through the link to the file that
it names, atomically, and the link stays. Three cases stop the command with the path of the link and the
path of the target. The target does not exist. Another user owns the target: install, `doctor --fix`,
and uninstall compare the owner of the target with the current user and do not change the file. Or the
target cannot be written. The Linux kernel call `statx` gives the owner, so the check runs on Linux
only. A system with a C library older than glibc 2.28 has no `statx`. There the command stops with a
message that names the missing call and tells the operator to replace the link with a regular file. It does
not write, because it cannot check the owner. A link that points to a missing file, or that loops, never
crashes `netclaw doctor`. The check `Daemon Env File Mode` reports it as a warning with the path, and
`doctor --fix` plans no fix for it. `netclaw doctor` reports a `daemon.env` that group or others can access.
`doctor --fix` sets mode 0600 on it and changes nothing else. When one fix cannot be written, `doctor --fix`
applies the other fixes, prints the path and the reason of the failed fix, and exits with code 1. The code
is 1 also when the checks found only warnings.

Only three files are owner-only: `daemon.env`, `secrets.json`, and `devices.json`. They hold plain-text
or secret values. `AtomicFile.WriteAllTextOwnerOnly` creates the temporary file with mode 0600, so the
content is never readable by group or others, not even before the rename. Every other file keeps the
default mode of a new file. This includes `netclaw.json`. A container can run the daemon as a user that
does not own a bind-mounted `netclaw.json`, for example uid 1654 with a read-only single-file mount, and
that user must read the file. `AtomicFile.WriteAllText` creates the temporary file with the mode of the file
that it replaces, so a rewrite is never more open than the file before it, and a rewrite of an owner-only
`netclaw.json` stays owner-only.

`netclaw daemon uninstall` stops the service and removes the unit file. It removes `daemon.env`
only when every line is blank, is a comment that starts with `#` or `;`, or starts with `PATH=`.
Leading spaces do not count. Any other line keeps the file, for example `export PATH=/a`. Uninstall
then prints the path and sets mode 0600. When uninstall cannot finish this step, it still removes the unit and
runs `daemon-reload`. It then prints the path and the reason, and exits with a non-zero code. The message names
the step that failed. It says "could not secure" for a kept file whose mode cannot change, for example a link to a
file that another user owns. It says "could not delete" for a PATH-only file that cannot go. It says "left in
place" for a link to a missing file or a link loop. Uninstall leaves `secrets.json` and `netclaw.json` in
place.

The install scripts honor `NETCLAW_HOME` with the `NetclawPaths` rules (`~`, `$HOME`, `${HOME}`,
and `%USERPROFILE%` expand, a blank value is unset, a relative path resolves against the working
directory). Both scripts use `<home>/config`. A fresh `install.sh` also uses `<home>/bin` and
`<home>/env`. An upgrade does not move an install. The order of the rules: `INSTALL_DIR` always wins.
Else an install that sits in `<home>/bin` is upgraded there. Else, if `$HOME/.netclaw/bin` holds
`netclaw` or `netclawd`, `install.sh` upgrades there, keeps the env script at `$HOME/.netclaw/env`, and
prints the directory. Else it is a fresh install under `<home>`. `install.ps1` keeps its
own install directory. `CONFIG_DIR` and `NETCLAW_CONFIG_DIR` no longer have an effect.

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
DaemonClient → ChatClientActor
    ↕ owns the transport
SignalR Client Adapter
    ↕ connects to
Daemon SignalR Hub (/hub/session)
```

`ChatViewModel` posts inputs and uses `InvokeAsync` for display changes.
It owns no connection loop, attachment flag, or text queue.
`ChatClientActor` uses behavior switches and `IWithTimers` for elapsed deadlines.
Tests use `TestScheduler`. The injected `TimeProvider` supplies UTC timestamps.
The event pump delivers status and output outside the actor.
A blocked observer cannot stop an actor command.

See [the current chat architecture](../architecture/chat-client.md) and
[the glossary](GLOSSARY.md#text-admission) for the admission boundary.

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
5. **After restart**: register fresh restart reminders. The normal reminder
   route activates each target session.
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
- Network binding / exposure mode

### Sources of Config Changes

- `netclaw init` TUI wizard (writes incrementally per section)
- Manual file editing by operator
- Not agent file tools: tool authorization write-denies the config directory
  (see `docs/architecture/tool-authorization.md`)

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
- Security controls: SPEC-003 (pointer to `docs/architecture/tool-authorization.md`)
- CLI contract: SPEC-004
- Operator UI: SPEC-005
- Gateway exposure: SPEC-006
- Guided onboarding: SPEC-007
