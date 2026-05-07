---
name: netclaw-operations
description: "REQUIRED when the user asks about scheduling, reminders, cron jobs, timers, background jobs, diagnostics, troubleshooting, MCP tools, daemon health, identity updates, or Netclaw capabilities and self-maintenance."
metadata:
  author: netclaw
  version: "1.25.0"
---

# Netclaw Operations

This is your operational guide. Load it when the user's request is about
Netclaw itself — what it can do, how to schedule work, how to diagnose
problems, how to update preferences, or how to maintain itself.

## Route by Intent

| User intent | Section below |
|-------------|---------------|
| Schedule reminders, cron jobs | [Scheduling](#scheduling) |
| Work on a project, switch projects | [Project Directory](#project-directory) |
| Discover MCP tools | [Tool Discovery](#tool-discovery) |
| Understand approval prompts | [Approval Prompts](#approval-prompts) |
| Manage skills and sources | [Skill Management](#skill-management) |
| Something is broken, debug it | [Diagnostics](#diagnostics) |
| Update preferences, tone, profile | [Identity](#identity) |
| Pair remote devices, manage access | [Device Pairing](#device-pairing) |
| Check health, update self | [Self-Maintenance](#self-maintenance) |
| Run long shell commands in background | [Background Jobs](#background-jobs) |
| Manage inbound webhooks | [Webhook Management](#webhook-management) |
| Search backend errors, configure SearXNG | [Search Providers](#search-providers) |

## Project Directory

Sessions track a **project directory** — the root of the codebase or project
you are currently working on. When set, the project's identity file (checked in
order: `.netclaw/AGENTS.md`, `CLAUDE.md`, `AGENTS.md`, `CONTEXT.md` — first
match wins) is automatically loaded into the system prompt alongside the global
SOUL/AGENTS/TOOLING layers.

Use `set_working_directory` to set or change the project directory:

```
set_working_directory(path: "/home/user/workspaces/akadonic")
```

Rules:

- The path must be an absolute path to an existing directory
- The path must be within the session's allowed file access roots
- Profile-managed: not available to Public or Team audiences by default
- Project identity files are re-read from disk on each `SetSystemPrompt()` call,
  so edits to the project's `AGENTS.md` take effect on the next project switch
  or daemon restart
- The project directory persists across crash/restart via `WorkingContext`
- The `[working-context]` block includes `project_dir:` so you always know which
  project is active

The project directory is distinct from the session directory
(`~/.netclaw/sessions/{id}/`). The session directory is immutable and used for
state isolation (inbox, media). The project directory is mutable and points to
the project root.

## Scheduling

Scheduling is gated on `Scheduling.Enabled` in `netclaw.json` (default `true`).
When disabled, reminder tools are hidden, `ReminderManagerActor` skips startup
reconciliation, and fired reminders are acknowledged but not executed. Public
audience sessions cannot use scheduling tools regardless of the config flag.

`set_reminder` accepts three schedule types:

| Type | Examples |
|------|---------|
| `once` | `"30m"`, `"2h"`, `"2026-03-15T14:30:00Z"` |
| `interval` | `"30m"`, `"6h"`, `"1d"` |
| `cron` | `"0 */6 * * *"`, `"0 9 * * MON-FRI"` |

Delivery contract parameters:

- `delivery_kind`: required, one of `current_session`, `channel`, `none`
- `delivery_transport`: required when `delivery_kind=channel` (e.g. `slack`)
- `delivery_address`: required when `delivery_kind=channel` (`#channel`, `@user`, or canonical ID)
- `delivery_required`: optional bool, default `true`; set `false` only for audit/cleanup tasks
- `delivery_instructions`: optional content guidance only (never routing)
- `expires_in`: optional recurring-reminder expiry duration (for `interval`/`cron` only), e.g. `"24h"`, `"7d"`

You may also pass the structured form `delivery: { kind, transport?, address? }`
instead of the three flat delivery fields.

Rules:

- Always choose `delivery_kind` explicitly.
- Do not try to route via `delivery_instructions`.
- `current_session` is the session check-back path and should be preferred for
  conversational follow-ups in Slack/TUI/SignalR sessions.
- `channel` requires both transport + address and resolves names/handles to
  canonical IDs at set time; unresolved targets fail loud.
- `none` runs silently (history still records execution).
- `expires_in` is not valid for `once` reminders; omit it for one-shot schedules.
- For recurring reminders that are permanently complete (PR merged, deploy done,
  incident resolved), call `cancel_reminder` with that reminder's ID so it does
  not keep firing indefinitely.

`cancel_reminder` **disables** the reminder — it stops future executions but
preserves the definition file on disk for diagnosis and re-enablement. To
permanently delete a reminder and its history, use the CLI:

```
netclaw reminder delete <id>
```

The `cancel` CLI subcommand mirrors the tool behavior (disable only):

```
netclaw reminder cancel <id>     # disable, keep definition
netclaw reminder delete <id>     # permanent delete + history
```

Reminders that hit 5 consecutive execution failures are auto-disabled with a
`ReminderAutoDisabled` critical alert. The definition stays on disk so the
operator can diagnose and re-enable after fixing the root cause.

If `audience` is omitted during conversational scheduling, the reminder inherits
the audience of the channel/session that created it. A reminder cannot be
minted with broader audience than the creator currently holds; lowering the
audience is always allowed.

Other scheduling tools: `list_reminders`, `cancel_reminder`,
`get_reminder_history`.

### Approval Requirements for Reminders and Webhooks

Reminders and webhooks execute without a human present — they CANNOT prompt for
tool approval. If a reminder needs `shell_execute` or file tools, those command
patterns must be pre-approved in `~/.netclaw/config/tool-approvals.json` BEFORE
the reminder fires.

**Before creating a reminder that uses shell commands:**
1. Identify what commands the reminder will need (e.g. `git pull`, `curl`,
   `cat /var/log/app.log`)
2. Run those commands interactively in the current session — this triggers the
   approval prompt and persists the grant
3. Then create the reminder

If the user has already approved the patterns in a previous session, no action is
needed — grants persist across sessions.

**Path restrictions:** Even with an approved command pattern, reminders are sandboxed to
trust zone paths (session dir, workspaces, project directory, skills, identity).
A reminder approved for `cat /srv/app/log.txt` can read that file inside trust
zones but NOT arbitrary system paths like `/etc/shadow`. If a reminder needs
access outside trust zones, the user must configure additional trusted roots.

**If a reminder fails with `command_not_pre_approved`:** The command pattern was
not in the approval store. Run the command interactively to trigger approval,
then the next reminder firing will succeed.

**If a reminder fails with `path_outside_trust_zone`:** The command targets a
path outside the allowed roots. Either move the target into a workspace, or ask
the user to add the path to trusted roots in config.

## Background Jobs

Shell commands expected to run longer than the session timeout can be submitted
as background jobs. Background jobs run independently of the session — results
are delivered asynchronously when the job completes, even if the session was
passivated.

To submit: set `_background: true` in the `shell_execute` tool call metadata.
Approval gates are evaluated before the job starts.

Rules:

- Only `shell_execute` supports background mode. Other tools ignore `_background`.
- `_timeout_seconds` alone does NOT trigger background execution.
- The user must approve the command before it starts running in the background.
- Maximum 5 concurrent background jobs; overflow queues FIFO.
- Job definitions persist to `~/.netclaw/jobs/{id}.json`.
- Output captured to `~/.netclaw/jobs/{id}/output.log`.

Monitoring tools:

- `check_background_job(JobId: "id")` — query status, elapsed time, output tail
- `check_background_job(JobId: "id", Cancel: true)` — cancel a running job

`check_background_job` is only available when shell execution is granted (same
`shell` grant category). It validates that the requesting session matches the
submitting session's audience and boundary.

After submitting a background job, schedule a check-back reminder so you report
results proactively when the job completes.

Active background jobs appear in the `[active-background-jobs]` section of the
session context on every turn.

## Tool Discovery

MCP tools are not loaded by default. Use `search_tools` to discover them:

```
search_tools(query: "servers")                  # list all MCP servers
search_tools(query: "all", server: "notion")    # browse a server's tools
search_tools(query: "email")                    # keyword search
```

After discovery, matched tools become callable for the session.

Sessions receive granted tool categories. `builtin` is always granted.
Other categories (`web`, `file`, `shell`, `scheduling`) depend on ACL
config. If a tool is missing, it may not be granted for this session.

Tools belonging to disabled subsystems (see [Feature Kill Switches](#feature-kill-switches))
are hidden from `search_tools` results for all audiences. Public sessions
additionally cannot discover or load skills, subagents, memory tools, or
scheduling tools regardless of feature flags.

### Adding MCP servers (fail-closed by default)

`netclaw mcp add` writes new MCP servers with **zero granted tools** and
per-audience approval defaults so freshly added servers are never silently
exposed:

| Audience | Grants | Approval default |
|----------|--------|------------------|
| Personal | `[]` (empty list — all tools denied until the operator opts in) | `Approval` |
| Team     | `[]` | `Approval` |
| Public   | `[]` | `Deny` |

After `mcp add`, the operator uses `netclaw mcp permissions` — an
interactive TUI — to grant specific tools and adjust the per-tool or
per-server approval mode. Bare `netclaw mcp tools` is a read-only CLI view
of the same state; both commands surface a discoverability hint toward the
TUI.

Escape hatch: `netclaw mcp add --grant-all` keeps the legacy "null grants
= all tools pass" behavior for CI. Even with `--grant-all`, the per-audience
approval defaults (Personal/Team=Approval, Public=Deny) are still written —
you cannot turn off the approval prompts at `mcp add` time.

Inside the TUI (`netclaw mcp permissions`):

- `Enter` toggles the highlighted tool's grant
- `A` toggles all tools on/off for the current audience
- `E` enables/disables the whole server for the current audience
- `M` cycles the **server default** approval mode (`Auto → Approval → Deny → Auto`)
- `P` cycles the **highlighted tool's explicit override** (`inherit → Auto → Approval → Deny → inherit`) — `inherit` removes any explicit override so the tool inherits the server default
- `S` saves pending changes to `netclaw.json`
- `←/→` cycles the selected audience

Approval-mode resolution precedence (for MCP tools):

1. Exact `ToolOverrides["{server}/{tool}"]` override
2. `McpServerDefaults[{server}]` default
3. Fail-closed fallback (Personal audience, shell/file-edit matcher family)
4. Audience `DefaultMode`

Newly discovered tools on an existing server automatically inherit the
server default; you do not need to re-run `permissions` after the server
learns a new tool.

### Migrating existing MCP servers

Servers added to `netclaw.json` before this behavior shipped stay untouched —
their tool grants, `ApprovalPolicy.McpServerDefaults`, and `ToolOverrides`
entries are not rewritten during an upgrade.

`netclaw doctor` will emit a warning for each enabled MCP server that
Personal can reach (`McpServersMode = All`) but has no
`ApprovalPolicy.McpServerDefaults[server]` entry and no `notion/*`-style
`ToolOverrides` entry. The warning points at `netclaw mcp permissions`.

To resolve: run `netclaw mcp permissions`, pick the server, switch to the
Personal audience, press `M` to set a server default (`Approval` is the
safe choice), `S` to save, then restart the daemon. Repeat for each
audience you want to tighten. `doctor` stops warning once the default is
set.

## Approval Prompts

Shell and file tool approvals are **per-binary-and-arguments** by design, not
per-binary. `sleep 5` and `sleep 10` are distinct approval patterns. So are
`rm foo.txt` and `rm bar.txt`, and `kill 12345` and `kill 67890`. This is not
a bug — it is the security gate.

The same extraction rule that makes `sleep 5` prompt separately from `sleep 10`
is what makes `rm foo.txt` prompt separately from `rm ~/.netclaw/netclaw.db`
and `kill 12345` prompt separately from `kill $(pgrep netclawd)`. Weakening
the rule for a "harmless" binary like `sleep` would require a hardcoded
allowlist of inert binaries, and any such list would become a silent
privilege-escalation path the moment an entry turned out not to be truly
inert (`ls` sees directory contents, `echo` can redirect via the shell,
`date` can be aliased). **Do not propose an inert-binary bypass list.** If
the prompt cadence is annoying, the right response is to approve each
pattern once and move on — grants persist in `~/.netclaw/config/tool-approvals.json`
so the noise is bounded.

File tool approvals (`file_write`, `file_edit`) use the same per-target rule:
one grant per path. That is the feature, not the bug — a file edit is a
file edit, and approval should be scoped to the target.

If a user asks why they're being prompted so often, explain the security
tradeoff and point them at `netclaw` CLI tooling for reviewing and trimming
`tool-approvals.json` if the grant list grows unmanageable.

If approval matching seems stale after an upgrade, delete the approval file and
rebuild grants interactively:

macOS/Linux:

```bash
rm ~/.netclaw/config/tool-approvals.json
```

PowerShell:

```powershell
Remove-Item "$HOME/.netclaw/config/tool-approvals.json" -Force
```

Then restart the daemon so in-memory session approvals are cleared too.

## Skill Management

The `netclaw skill` CLI manages skills and skill sources. All subcommands
are offline — no daemon required.

| Command | What it does |
|---------|--------------|
| `netclaw skill list` | List all discovered skills with source, version, status |
| `netclaw skill show <name>` | Show skill metadata and full content |
| `netclaw skill validate <path>` | Validate a SKILL.md file's frontmatter format |
| `netclaw skill remove <name>` | Remove a native skill (refuses system/external) |
| `netclaw skill issues` | Show only scanner issues (rejected items with reasons) |
| `netclaw skill search <query>` | Search skills by name or description |

### External skill sources

Register additional skill directories (e.g. `~/.claude/skills/`):

| Command | What it does |
|---------|--------------|
| `netclaw skill source list` | Show configured external sources |
| `netclaw skill source add <name> --well-known claude-code` | Add Claude Code skills |
| `netclaw skill source add <name> --path /shared/skills` | Add a custom directory |
| `netclaw skill source remove <name>` | Remove a source |
| `netclaw skill source enable <name>` | Enable a disabled source |
| `netclaw skill source disable <name>` | Disable without removing |

The daemon's `SkillDirectoryWatcherService` automatically rescans all skill
directories (native + external) when files change on disk. No restart needed.

## Webhook Management

Webhooks are gated on `Webhooks.Enabled` in `netclaw.json` (default `true`).
When disabled, the webhook HTTP endpoint returns 404 for all routes and
webhook tools are hidden from discovery.

Inbound webhooks use a split config model:

- `~/.netclaw/config/netclaw.json` -> `Webhooks.Enabled` toggles the feature
- `~/.netclaw/config/webhooks/*.json` -> one route per file; filename is the
  route name used at `/api/webhooks/{route}`

Use the dedicated tools instead of generic file tools when available:

- `set_webhook`
- `list_webhooks`
- `delete_webhook`

When using `set_webhook`, use `delivery_required` (bool, default `true`) to
control required notification behavior. `notify_policy` is deprecated.

Route files are secret-bearing config because they may contain inline
verification secrets. Treat `config/webhooks` like `secrets.json` and avoid
broad file reads/writes there unless the user explicitly wants raw config work.

Verification kinds are generic:

- `Hmac`
- `HeaderSecret`

Route files hot-reload without restarting the daemon. If a route file becomes
invalid, Netclaw removes that route immediately and emits an operational alert.

**Approval gate:** Webhooks run without a human — they cannot prompt for
approval. The same rules as reminders apply: commands must be pre-approved in
`tool-approvals.json` and path arguments must be within trust zones. See
"Approval Requirements for Reminders and Webhooks" in the Scheduling section.

### Webhook observability

`netclaw stats` includes a `webhooks:` section with:

- **Route counts** — `total`, `enabled`, `disabled`, `invalid` (files on disk,
  classified by parse/validation status plus the per-route `Enabled` flag).
- **Delivery counters** — `accepted`, `filtered` (event not in allowlist),
  `duplicate` (delivery id already seen), plus per-rejection counts:
  `404` (route_not_found), `401` (verification_failed), `413` (body_too_large),
  `400` (invalid_json), `429` (rate_limited).

Every ingress outcome writes a structured line to `daemon-{date}.log`:

```
Webhook rejected route={Route} reason={Reason} remote_ip={RemoteIp}
  delivery_id={DeliveryId} event_type={EventType}
```

Rejection paths only log + increment counters — they do NOT fire outbound
operational notifications, so bad or adversarial traffic does not spam the
configured notification target.

## Inbound Attachments

When a user sends a file in Slack, Netclaw runs the attachment through an
ingress pipeline before it reaches the LLM:

1. **Policy gate** — checks audience, file category, and per-message file count
   against `ChannelAttachmentPolicy`
2. **Size gate** — rejects files above the per-audience byte limit
3. **Download** — fetches from Slack's private file API with bot-token auth
4. **Content scan** — runs the configured `IContentScanner` (e.g. antivirus)
5. **Inbox write** — saves to `~/.netclaw/sessions/{session-id}/inbox/`
6. **Announcement line** — appends `[attachment]` text to the user turn

The `[attachment]` line format is:
```
[attachment] name="..." mime="..." size=N path="inbox/..." inlined="true|false"
```

`inlined="true"` means the file bytes were forwarded to the model as
`DataContent` (images and PDFs on vision-capable models). `inlined="false"`
means the model only sees the path reference.

**Historical thread backfill** follows the same download/scan flow for all
file types (not just images) — PDFs and other documents from prior thread
messages are included.

**When attachment ingress fails**, Netclaw posts a stable user-facing message
(e.g. "Couldn't download `file.pdf` — please try again later.") and logs the
full exception internally. Exception details are **never** forwarded to Slack.

| Symptom | Check |
|---------|-------|
| File rejected before download | audience/category policy gate; check `ChannelAttachmentPolicy` config |
| Download timeout | bot token valid? Slack network reachable? check `daemon-{date}.log` |
| Content scan rejection | `netclaw status` scanner section; check scan config |
| Inbox write failure | disk space? permissions on `~/.netclaw/sessions/`? |

## Search Providers

The `web_search` and `web_fetch` tools route through one configured search
backend, selected by `Search.Backend` in `netclaw.json`:

| Backend | Shape | Notes |
|---------|-------|-------|
| `SearXng` | Self-hosted | Operator runs the instance; `Search.SearXngEndpoint` points at it. JSON output must be enabled in the instance's `settings.yml`. Authenticated instances are not supported in current releases. |
| `Brave`   | Managed | Requires `Search.BraveApiKey` in `secrets.json`. |
| `DuckDuckGo` | Scraped | No config; least reliable, may hit bot detection. |

When a search tool returns an error mentioning `settings.yml`,
`search.formats`, or "rate limit exceeded", the operator's SearXNG instance
is misconfigured or being throttled. Point them at the canonical setup
guide:

```
https://netclaw.dev/docs/configuration/search-providers/
```

That page lists the supported `settings.yml` keys, reverse-proxy header
requirements (Netclaw's outbound `User-Agent` is `Netclaw/{version}
(+https://netclaw.dev)` — non-empty UAs must pass), and the limiter
behavior we honor (HTTP 429 + `Retry-After`).

For Brave, an authentication error surfaces as "API authentication failed"
— the fix is updating `Search.BraveApiKey` in `secrets.json`.

## Diagnostics

When something seems wrong with Netclaw itself:

1. Run `netclaw doctor` via `shell_execute` — validates config, providers,
   MCP connections, memory health, and recent daemon crash logs
2. If doctor reports fixable issues, run `netclaw doctor --fix --dry-run` to
   preview auto-repairs (schema-driven: stale properties, enum coercion, missing defaults)
3. Run `netclaw status` via `shell_execute` — live runtime state from daemon
3. Check daemon logs at `~/.netclaw/logs/daemon-{yyyy-MM-dd}.log`
4. Check session logs at `~/.netclaw/sessions/{session-id}/logs/`

| Symptom | Check |
|---------|-------|
| No LLM responses | `netclaw doctor`; verify provider credentials |
| Missing tools | `netclaw mcp list`; check MCP connection state |
| Memory recall degraded | `netclaw status` memory section |
| Daemon won't start | crash logs at `~/.netclaw/logs/crash-*.log` |

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

Config files: `~/.netclaw/config/netclaw.json` (daemon-owned base config,
including `Daemon.Host`, `Daemon.Port`, `Daemon.ExposureMode`),
`~/.netclaw/client/config.json` (local CLI endpoint state),
`~/.netclaw/config/secrets.json` (credentials — never display API keys).

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

## Identity

Your identity is defined by layered files loaded into the session prompt:

| Layer | Source | Audience |
|-------|--------|----------|
| SOUL.md | `~/.netclaw/identity/SOUL.md` (filesystem) | All |
| AGENTS.md | Embedded in the Netclaw binary (audience-specific) | Team/Personal get full version; Public gets stripped version |
| TOOLING.md | `~/.netclaw/identity/TOOLING.md` (filesystem) | Team/Personal only |
| Project instructions | `.netclaw/AGENTS.md` etc. in project directory | Team/Personal only |

**AGENTS.md is binary-owned.** The full AGENTS (Team/Personal) contains
operating rules, autonomy guidance, grounding, search policy, scheduling,
background shell, subagent delegation, skill reference, identity file paths,
and memory triage. The Public AGENTS contains only basic operating rules,
autonomy, grounding, and media attachment guidance — no scheduling, subagent,
skill, identity-path, memory, search, or background-shell sections.

SOUL.md and TOOLING.md remain editable on disk:
- To edit: read the file first with `file_read`, then write with `file_write`.
- Detail subdirectories: `identity/soul/`, `identity/tooling/`.

**Identity vs memory:** If it should shape every future session → identity
file. If it should be recalled when relevant → SQLite memory.

## Self-Maintenance

| Action | Command (via `shell_execute`) |
|--------|-------------------------------|
| Check for updates | `netclaw update` |
| Self-diagnose | `netclaw doctor` |
| Runtime health | `netclaw status` |
| Memory/token stats | `netclaw stats` |
| Historical skill usage by method/name | `netclaw stats skills` |
| List/manage skills | `netclaw skill list` |
| List past sessions | `netclaw sessions --once` |
| Inspect reminder history | `netclaw reminder history <id> --last 5` |
| Permanently delete a reminder | `netclaw reminder delete <id>` |

## Device Pairing

Remote devices authenticate with the daemon using a two-sided pairing protocol.

### Pairing flow

**Daemon side** (requires local/SSH access):

```
shell_execute: netclaw daemon pair
```

This generates a single-use pairing code (8 chars, 5-minute TTL). The code
generation endpoint is loopback-only.

If `netclaw daemon pair` fails immediately after an exposure-mode change, run
`netclaw doctor` and inspect `~/.netclaw/logs/crash-*.log` for the specific
startup validation failure instead of assuming a generic readiness timeout.

**Client side** (remote device):

```
shell_execute: netclaw pair https://my-daemon.tail1234.ts.net:5000
```

The user is prompted for the pairing code. On success, the bearer token is
saved to `secrets.json` (`DeviceToken` field) and the endpoint is saved to
`~/.netclaw/client/config.json`.

### Device management

| Action | Command |
|--------|---------|
| List paired devices | `netclaw daemon devices` |
| Revoke a device | `netclaw daemon devices revoke <name>` |

### Security notes

- Codes: single-use, 8 chars from 32-char alphabet (~1.1 trillion
  combinations), 5-minute TTL
- Rate limiting: 5 attempts/min/IP; after 10 failures, the IP is blocked for
  15 minutes
- When no code is pending, the exchange endpoint returns 404 (invisible to
  scanners)
- Tokens are stored as salted SHA-256 hashes on the daemon; the raw token is
  never persisted server-side

### Config locations

- `~/.netclaw/config/devices.json` — paired device registry (daemon side)
- `~/.netclaw/config/secrets.json` — `DeviceToken` field (client side, added
  by `netclaw pair`)
- `~/.netclaw/config/netclaw.json` — `Daemon` section (`Host`, `Port`,
  `ExposureMode`)
