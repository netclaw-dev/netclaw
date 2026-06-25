# Diagnostics, Kill Switches & Self-Maintenance


## Diagnostics


When something seems wrong with Netclaw itself:

1. Run `netclaw doctor` via `shell_execute` — validates config, providers,
   MCP connections, memory health, and recent daemon crash logs
2. If doctor reports fixable issues, run `netclaw doctor --fix --dry-run` to
   preview auto-repairs (schema-driven: stale properties, enum coercion, missing defaults)
3. Run `netclaw status` via `shell_execute` — live runtime state from daemon
3. Check daemon logs at `~/.netclaw/logs/daemon-{yyyy-MM-dd}.log`
4. Check session logs at `~/.netclaw/logs/sessions/{sanitized-session-id}/session.log`

Log split:

- Daemon-global diagnostics stay in the daemon log (rolled daily, capped
  at 10 MB per file).
- Session-owned diagnostics and session output audit trails append to
  `~/.netclaw/logs/sessions/{sanitized-session-id}/session.log` —
  one file per session, no rotation today (see netclaw-dev/netclaw#919).
- Session log directories use the sanitized session ID (`/`, `.`, spaces,
  etc. replaced with `_`). Sub-agent diagnostics roll up into the parent
  session's `session.log`; you will not find a separate file for a
  sub-agent run.

What to expect inside `session.log`:

- A single chronological timeline. One actor (`SessionLogActor`) is the
  only writer per file, so audit lines and diagnostic lines interleave in
  wall-clock order — useful for reading "what happened, in order" without
  cross-referencing two files.
- Two line shapes: session output audit lines (`User:`, `Assistant:`,
  `Thinking:`, `Tool call:`, `Tool result:`, `Usage:`, `Turn N completed`,
  etc.) and `Diagnostic:` lines from MEL providers (LLM client, HTTP,
  retry middleware) emitted under a session diagnostics scope.
- Best-effort observability. Individual lines may be dropped on transient
  IO errors and logged at Debug level in the daemon log; this is not a
  transactional audit trail. Cross-check the daemon log for warnings
  if a critical line appears missing.
- Sidecar paths (compaction, title generation, sub-agents, memory
  distillation) currently bypass the session diagnostics scope, so their
  internal diagnostics may not appear in `session.log` even though their
  output audit lines do. Tracked in netclaw-dev/netclaw#920.

| Symptom | Check |
|---------|-------|
| No LLM responses | `netclaw doctor`; verify provider credentials |
| Missing tools | `netclaw mcp list`; check MCP connection state |
| Memory recall degraded | `netclaw status` memory section |
| Daemon won't start | crash logs at `~/.netclaw/logs/crash-*.log` |
| Docker daemon cannot create `/home/netclaw/.netclaw/*` | Official image entrypoint repairs writable bind mounts to UID/GID `1654:1654`; if bypassed or read-only, run `sudo chown -R 1654:1654 <host-data-dir>` or use a Docker named volume |
| Discord/Slack channel offline | `netclaw status` shows the channel `disconnected` with a reason. Discord may also report `degraded` when Discord.Net says the socket is connected but the gateway is not ready, such as after a resumed session that Netclaw is replacing with a clean reconnect. A misconfigured channel (bad token, missing Discord Message Content intent) degrades only that channel — the daemon keeps running and other channels are unaffected. A transient network failure retries automatically; a config/permission failure stays offline until the operator fixes the config and restarts the daemon. |
| `command not found` for `netclaw` from shell tool when daemon runs as systemd service | `netclaw doctor` (the **Systemd Unit PATH** check warns when the unit was installed before PATH was baked in) |

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
