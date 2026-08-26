# Diagnostics, Kill Switches & Self-Maintenance


## Diagnostics


When something seems wrong with Netclaw itself:

1. Run `netclaw doctor` via `shell_execute` — validates config, providers,
   MCP connections, memory health, and recent daemon crash logs
2. If doctor reports fixable issues, run `netclaw doctor --fix --dry-run` to
   preview auto-repairs (schema-driven: stale properties, enum coercion, missing defaults)
3. Run `netclaw status` via `shell_execute` — live runtime state from daemon
3. Check daemon logs at `<NETCLAW_HOME>/logs/daemon-{yyyy-MM-dd}.log` (`NETCLAW_HOME` defaults to `~/.netclaw`)
4. Check session logs at `<NETCLAW_HOME>/logs/sessions/{sanitized-session-id}/session.log`

If `netclaw status` or `netclaw chat` prints `daemon not configured - please run
netclaw init`, do not troubleshoot daemon reachability or model defaults. The
install has no `netclaw.json`; run `netclaw init` first. If doctor prints the
same message in the config-file check, treat it as the same uninitialized-install
state.

Log split — one stream, partitioned locally by session:

- A log line that carries a session id (an actor's `WithContext("SessionId", …)`,
  a `{SessionId}` message field, or a `SessionId` logging scope) is written to that
  session's `session.log` and **not** to `daemon.log`. The partition is by session
  id — nothing is duplicated locally.
- `daemon.log` holds only sessionless, daemon-wide lines: startup/config, session
  start/stop, and operational **alerts** (e.g. the `provider.unreachable` /
  `provider.failover` alert raised when an inference provider goes down — surfaced
  here, and to webhooks, by the notification sink). Note the *per-call* failover/retry
  log lines emitted while serving a specific session carry that session's id, so they
  partition into its `session.log`; the daemon-wide outage signal is the alert in
  `daemon.log`. Rolled daily, capped at 10 MB per file.
- The **full** stream (daemon and session lines alike) is also exported to OTEL/Seq
  with the session id as an attribute; do the global slicing/distilling on the OTEL
  receiver side.
- Session log directories use the sanitized session ID (`/`, `.`, spaces, etc.
  replaced with `_`). Each **sub-agent run** writes to its **own** `session.log`,
  keyed by its sub-session id (`{parentId}/subagent/{name}/{runId}`, sanitized) —
  so a sub-agent's detail stays out of the parent's log and you review it in that
  run's own file. The parent's `session.log` keeps the spawn breadcrumbs (requested
  → spawned → completed/failed) as the pointer to each run. In OTEL the sub-agent
  lines still carry the parent `SessionId` (so they group under the parent) plus
  `SubSessionId` (so they slice by run). No rotation today
  (see netclaw-dev/netclaw#919).

What to expect inside `session.log`:

- The session's **full local slice** of the log stream, in wall-clock order: the
  conversation audit (`User:`, `Assistant:`, `Thinking:`, `Tool call:`,
  `Tool result:`, `Usage:`, `Turn N completed`) interleaved with every operational
  line scoped to that session — the LLM pipeline, retries, provider failover, tool
  and sub-agent activity (spawn requested → child spawned → completed/failed, plus
  guard rejections), memory, etc. One actor (`SessionLogActor`) is the only writer
  per file.
- Because the partition is by session id, you usually do **not** need to grep — open
  the one file for the session and read top to bottom. To correlate across sessions
  or globally, use Seq/OTLP (every line is there with `SessionId` as a field).
- The session-log writer's own failure lines are the one exception: they go to
  `daemon.log`, never routed back into the file that just failed.
- Writes are split by kind. The **conversation audit** (User/Assistant/Tool/Usage
  lines) is flushed **immediately**, so a hard process death cannot drop the audit
  tail. The higher-volume **diagnostics** are **batched** (flushed on a ~1s cadence),
  so a recent diagnostic line may lag by up to a second. Individual lines may be
  dropped on transient IO errors (a warning lands in `daemon.log`).

What stays in `daemon.log`: only sessionless lines — daemon startup/config, session
lifecycle, and global errors. Debugging one session → read its `session.log`;
debugging a daemon-wide problem → read `daemon.log`.

| Symptom | Check |
|---------|-------|
| No LLM responses | `netclaw doctor`; verify provider credentials |
| Missing tools | `netclaw mcp list`; check MCP connection state |
| An MCP tool call fails | Grep `<NETCLAW_HOME>/logs/daemon-*.log` for `MCP tool '` and `invocation failed`. Each failed call writes one Warning that names the server, the tool, and the HTTP status when the server sent one. The tool-result text gives the kind: `reported a failure:` is an error the tool declared, and `failed:` is an exception from the server or the transport. |
| Memory recall degraded | `netclaw status` memory section |
| Daemon won't start | crash logs at `<NETCLAW_HOME>/logs/crash-*.log` (`NETCLAW_HOME` defaults to `~/.netclaw`) |
| Docker daemon cannot create `/home/netclaw/.netclaw/*` | Official image entrypoint repairs writable bind mounts to UID/GID `1654:1654`; if bypassed or read-only, run `sudo chown -R 1654:1654 <host-data-dir>` or use a Docker named volume |
| Discord/Slack channel offline | `netclaw status` shows the channel `disconnected` with a reason. Discord may also report `degraded` when Discord.Net says the socket is connected but the gateway is not ready, such as after a resumed session that Netclaw is replacing with a clean reconnect. A misconfigured channel (bad token, missing Discord Message Content intent) degrades only that channel — the daemon keeps running and other channels are unaffected. A transient network failure retries automatically; a config/permission failure stays offline until the operator fixes the config and restarts the daemon. |
| `command not found` for `netclaw`/`dotnet`/a user tool from the shell tool when the daemon runs as a systemd service | The systemd `--user` service does not inherit your login-shell `PATH`; `netclaw daemon install` captures it into `~/.netclaw/config/daemon.env`. Run `netclaw doctor` (the **Systemd Unit PATH** check flags a missing/stale/legacy env file), then `netclaw doctor --fix` to rehydrate `PATH` from your current shell (or re-run `netclaw daemon install`), and finally `systemctl --user restart netclaw`. Installed a new tool after install? Its dir won't be seen until you re-run one of those and restart. Per-directory managers (`mise`/`asdf`/`direnv`) are not captured. |

If webhook notifications are configured, daemon crash paths emit
`daemon.crashing` operational alerts with context (PID, reason, and latest known
session/turn snapshot when available).

Doctor checks include `exposure-mode`, which validates that the `Daemon`
config section (if present) specifies a supported exposure mode and that
the corresponding tunnel integration is reachable.

Exposure diagnostics are fail-closed:
- `reverse-proxy` requires at least one remote authentication path and rejects
  loopback final hops (`127.0.0.1`, `::1`, `localhost`) because loopback
  auto-auth is reserved for true local operator traffic.
- `Daemon.TrustedProxies` entries must be literal IPs or CIDR strings; malformed
  values fail loudly in schema validation, `netclaw doctor`, and daemon startup.
- Tunnel-backed modes (`tailscale-serve`, `tailscale-funnel`,
  `cloudflare-tunnel`) require their local tunnel process by default.
  `Daemon.SkipTunnelProcessCheck=true` is an explicit opt-in only for sidecar or
  host-managed tunnel topologies; all other exposure requirements still apply.
- The `netclaw config` Exposure Mode editor preserves dormant reverse-proxy
  values in `~/.netclaw/config/editor-state.json` when switching to `local` or a
  tunnel mode. Runtime-active `Daemon.Host` and `Daemon.TrustedProxies` are
  removed from `netclaw.json` while inactive so local startup validation remains
  loopback-only. Treat `editor-state.json` as passive editor state, not daemon
  configuration.

Network exposure is configured in `netclaw config` → Security & Access →
Exposure Mode — not in first-run `netclaw init`, which is a minimal bootstrap
(Provider → Identity → Security Posture → Enabled Features → Health Check).
The exposure editor offers all five modes — `local`, `reverse-proxy`,
`tailscale-serve`, `tailscale-funnel`, `cloudflare-tunnel`. Selecting
`reverse-proxy` collects `Daemon.Host` (must be non-loopback) and
`Daemon.TrustedProxies` (≥1 entry required, comma-separated). The editor
refuses to save past the trusted-proxies prompt with an empty list — the same
minimum the daemon validator enforces at startup — so an operator who does not
yet know their proxy IP can leave exposure at `local` and set the bind address
and trusted proxies later once the proxy topology is known.

Config files: `~/.netclaw/config/netclaw.json` (daemon-owned base config,
including `Daemon.Host`, `Daemon.Port`, `Daemon.ExposureMode`),
`~/.netclaw/client/config.json` (local CLI endpoint state),
`~/.netclaw/config/secrets.json` (credentials — never display API keys), and
`~/.netclaw/config/editor-state.json` (passive config-editor state for dormant
mode-specific values).

## Feature Kill Switches


Deployment-wide feature flags in `netclaw.json` disable entire subsystems
for all audiences. Each defaults to `true` (enabled).

| Config path | What it gates |
|-------------|---------------|
| `Memory.Enabled` | Recall, extraction, memory tools |
| `Search.Enabled` | `web_search`, `web_fetch` tools |
| `SkillSync.Enabled` | `skill_load`, `skill_read_resource`, skill index |
| `SubAgents.Enabled` | `spawn_agent`, subagent discovery |
| `Scheduling.Enabled` | Reminder tools, reminder execution, `ReminderManagerActor` startup |
| `Webhooks.Enabled` | Webhook ingress and webhook tools |

When a subsystem is disabled, its tools are hidden from `search_tools` for
ALL audiences (not just Public), and direct invocation returns a generic
denial. Context layers for disabled subsystems return empty content.

The `netclaw init` wizard presents a Feature Selection step for Team and
Public postures, allowing operators to pre-configure which subsystems are
active. Personal posture skips this step (all features enabled by default).

## Self-Maintenance


| Action | Command (via `shell_execute`) |
|--------|-------------------------------|
| Check for updates | `netclaw update` |
| Switch update channel (saved) | `netclaw update --channel beta` |
| Self-diagnose | `netclaw doctor` |
| Runtime health | `netclaw status` |
| Memory/token stats | `netclaw stats` |
| Historical skill usage by method/name | `netclaw stats skills` |
| List/manage skills | `netclaw skill list` |
| List past sessions | `netclaw sessions --once` |
| Inspect reminder history | `netclaw reminder history <id> --last 5` |
| Permanently delete a reminder | `netclaw reminder delete <id>` |

`netclaw update` preserves daemon ownership. When `netclaw.service` is active or
enabled as a systemd user service, update restarts it with `systemctl --user`
instead of launching a detached daemon. If restart fails, inspect
`systemctl --user status netclaw.service`, then start it manually with
`systemctl --user start netclaw.service` or fall back to `netclaw daemon start`
only when no systemd user service owns the daemon.
