---
name: netclaw-operations
description: "REQUIRED when the user asks about scheduling, reminders, cron jobs, timers, background jobs, diagnostics, troubleshooting, MCP tools, daemon health, identity updates, or Netclaw capabilities and self-maintenance."
metadata:
  author: netclaw
  version: "2.13.0"
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
| Add or switch LLM provider, OAuth login | [LLM Providers](#llm-providers) |
| Show / kick the tires on Netclaw end-to-end locally | [Demo AppHost](#demo-apphost) |
| Search backend errors, configure SearXNG | [Search Providers](#search-providers) |
| Rotate or repair secrets | [Secret Management](#secret-management) |

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
- Profile-managed: granted to Team and Personal audiences by default, not Public
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
- `delivery_transport`: required when `delivery_kind=channel` (e.g. `slack`, `discord`, `mattermost`)
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

### Proactive channel messaging

To start a brand-new conversation on a chat channel — a `delivery_kind=channel`
reminder firing, or unprompted cross-channel outreach ("let the team know") —
use the generic proactive-post tool:

- `send_channel_message(channel_key, destination, text)` posts through the
  selected enabled channel and creates a new conversation thread when that
  channel supports it.
- `destination` must be a resolved object with `channel_key`, `kind`, and `id`.
  Do not pass bare display names like `#general` or `@alice`.
- `destination.kind="destination"` posts to a channel/destination ID returned by
  `lookup_channel_destination`.
- `destination.kind="direct_message"` sends a DM using the stable user ID from
  `lookup_channel_user`; this is supported only by channels that advertise DM
  output (Slack, Discord, and Mattermost when enabled in config).
- Reminder- and webhook-originated turns may only call `send_channel_message`
  against the delivery target configured on the reminder or webhook route. If a
  trigger turn has no configured target, Netclaw fails loud instead of choosing a
  default output channel.

Use the generic lookup tools before sending when you do not already have a
stable channel/user ID:

- `lookup_channel_user(channel_key, query)` resolves users on enabled channels
  that support user lookup, currently Slack, Discord, and Mattermost.
- `lookup_channel_destination(channel_key, query)` resolves destinations on
  enabled channels that support destination lookup. Slack can resolve channel
  names and IDs; Discord can resolve channel mentions, stable IDs, and cached
  text-channel names; Mattermost requires an exact channel ID.
- **Discovery:** call `lookup_channel_destination` with `query` omitted (or
  blank) to list every destination the channel can currently deliver to —
  Slack lists the configured allowlist (the resolved default channel plus
  `AllowedChannelIds`) with display names resolved from the API and archived
  channels excluded, Discord lists ACL-allowed guild text channels, and
  Mattermost lists the configured allowlist. Use this to answer "where can I
  post?" before sending. Listing is destination-only: user directories are
  unbounded, so `lookup_channel_user` always requires a query.

Both tools require `channel_key` as the first argument. Use the returned
`channel_key` and `stable_id` exactly; for destination lookups, use the returned
`address_kind` (`destination`) as `destination.kind`. For user lookups, set
`destination.kind` to `direct_message` and pass the returned user `stable_id`.
If lookup is ambiguous, pick from the returned candidates instead of guessing.
Do not use channel-specific lookup aliases such as `lookup_slack_user` or
`lookup_mattermost_user`; lookup is intentionally routed through the generic
channel tools.

Examples:

```
send_channel_message(
  channel_key: "slack",
  destination: { channel_key: "slack", kind: "destination", id: "C0123ABC" },
  text: "Deployment finished successfully.")

send_channel_message(
  channel_key: "mattermost",
  destination: { channel_key: "mattermost", kind: "direct_message", id: "26characterMattermostUserId" },
  text: "Your report is ready.")

send_channel_message(
  channel_key: "discord",
  destination: { channel_key: "discord", kind: "direct_message", id: "123456789012345678" },
  text: "Your report is ready.")
```

### Approval Requirements for Reminders and Webhooks

Reminders and webhooks execute without a human present — they CANNOT prompt for
tool approval. The cwd at firing time will not match any cwd a user clicked
"Always here" for during interactive use, so folder-scoped approvals will not
match.

**Before creating a reminder that uses shell commands**, identify the verbs the
task will need (e.g. `freshdesk`, `curl`, `git pull`) and pre-approve them as
global wildcards. Two paths:

1. **Suggest `trust-verb` from the agent.** When you (the agent) are helping the
   user set up a scheduled task, identify the verbs the task will need and ask
   the user before pre-approving each one. Example:

   > "This reminder will need to call `freshdesk --since=24h` whenever it
   > fires. Mind if I pre-approve `freshdesk` as a global verb so the reminder
   > can run unattended? I'll do this with
   > `netclaw approvals trust-verb freshdesk`."

   On confirmation, run the trust-verb command via `shell_execute`. The grant
   becomes a `(verb, null)` entry — auto-approved for any cwd.

2. **Operator runs the CLI directly:** `netclaw approvals trust-verb <verb>`.
   Same outcome; useful when the user already knows what they want.

If the user has already trusted the verb in a previous session, no action is
needed — `(verb, null)` grants persist in `tool-approvals.json` across daemon
restarts.

**Path restrictions:** A trusted verb runs wherever the creating audience's
file-access policy allows — the same scoping `file_write` uses. A Personal
reminder/webhook (the default when created from a Personal session) has
unrestricted filesystem access; a Team or Public one is confined to its session
directory — and cannot run `shell_execute` at all, since shell is Personal-only.
Protected paths — `secrets.json`, `.netclaw/keys`, `config/webhooks` — are always
denied regardless of audience or pre-approval.

**If a reminder fails with `command_not_pre_approved`:** The verb is not in the
approval store as a global wildcard. Run
`netclaw approvals trust-verb <verb>` and the next firing succeeds.

**If a reminder fails with `path_outside_trust_zone`:** The command targets a
path outside the allowed roots. Either move the target into a workspace, or ask
the user to add the path to trusted roots in config.

## Background Jobs

A background job is a **detached process with no expectation of completion** —
use it for anything that outlives a single tool call: long builds, test suites,
dev servers, watchers. Output streams to the job's log file while it runs, and
you are notified whenever the process terminates, by whatever cause.

To submit: set `_background: true` in the `shell_execute` tool call metadata.
Approval gates are evaluated before the job starts. The submit result includes
the output log path.

Lifecycle:

- **No `_timeout_seconds` = no kill timer.** The job runs until it exits, you
  cancel it, or the session passivates. A positive `_timeout_seconds` arms an
  explicit kill timer.
- **Jobs are killed when the session passivates** (conversation idle past the
  idle timeout). A job killed this way shows as `reaped` in
  `[active-background-jobs]` on your next turn — its process is gone; resubmit
  if still needed (its output log remains readable). For work that must survive
  an idle conversation, use a scheduled task; check-back reminders also keep
  the session (and its jobs) alive across a long wait.
- On daemon restart, running jobs are killed and you receive a `lost`
  notification with the log path — relaunch if still needed.
- Process exit (success or failure) delivers a result turn with exit code,
  output tail, and log path — even if the session was passivated mid-flight.

Monitoring a running job (e.g. waiting for a dev server to come up):

- `file_read`/`grep` the output log — it streams live (secret-redacted,
  rotation-bounded) at `~/.netclaw/jobs/{id}/output.log`.
- `check_background_job(JobId: "id")` — status, elapsed time, live output tail
- Probe the service directly (e.g. curl the port) once the log shows it started.
- `check_background_job(JobId: "id", Cancel: true)` — cancel a running job.
  **Cancel servers and watchers when you are done validating** — do not leave
  them holding one of the 5 concurrent job slots.

Rules:

- Only `shell_execute` supports background mode. Other tools ignore `_background`.
- `_timeout_seconds` alone does NOT trigger background execution.
- For synchronous calls `_timeout_seconds` is honored as you set it (no ceiling
  or floor); when omitted, the default tool timeout applies. Background jobs
  differ: omitted means no timer at all.
- **Long-running delegation calls** (e.g. `curl` to a local coding-agent or
  model server that takes minutes to respond) should run as background jobs.
- The user must approve the command before it starts running in the background.
- Maximum 5 concurrent background jobs; overflow queues FIFO.
- Job definitions persist to `~/.netclaw/jobs/{id}.json`.

`check_background_job` is only available when shell execution is granted (same
`shell` grant category). It validates that the requesting session matches the
submitting session's audience and boundary.

After submitting a long finite job, schedule a check-back reminder so you report
results proactively when it completes.

Active background jobs appear in the `[active-background-jobs]` section of the
session context on every turn.

## Tool argument validation

Tool argument names are validated strictly — unrecognized keys reject the call
before execution with a `did you mean '<canonical>'?` suggestion and the list
of valid argument names. Meta keys are exact-match: `_timeout_seconds` and
`_background` (a leading underscore, snake_case). `TimeoutSeconds`,
`timeout_seconds`, or `_timeoutSeconds` are rejected, never silently dropped.
Values must parse as their declared type: `_timeout_seconds: "1200ms"` or
`_background: "yes"` rejects the call instead of silently using defaults. When
a call is rejected this way the tool did NOT run — fix the argument and
re-issue once; do not retry the same shape.

## Large tool output

Tool output is bounded to a small inline budget
(`Session.Tuning.MaxInlineToolResultChars`, default 2000 chars) so it never floods
the context window. When a tool's output exceeds that budget you get a head+tail
view inline plus a pointer to the full output — not the whole thing:

- **`shell_execute`** spills the full (redacted) output to
  `{session}/tool-calls/{toolCallId}.log` and gives you the path. Read a slice with
  `file_read` (`StartLine`/`Limit`) or `grep` it — do NOT re-run the command to see more.
- **`file_read`** on a large file returns the head and steers you to read a
  specific range with `StartLine`/`Limit` or `grep` (`StartLine` is a 1-based line
  number — line 1 is the first line). Don't `cat` a huge file through
  `shell_execute` to get around it — that just spills again.
- **`background_job`** output goes to `~/.netclaw/jobs/{id}/output.log` (bounded);
  `check_background_job` returns a tail, and you can `file_read`/`grep` the log for the rest.

Reading a targeted range or grepping is always cheaper than re-running a command or
re-reading a whole file. Secret-bearing values are redacted from all tool output.

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

Built-in tool grants follow the audience and are monotonic (Public ⊆ Team ⊆
Personal). **Public** sessions get read-only file tools only — `file_read`,
`file_list`, `attach_file` — and no outbound web access. **Team** adds
`file_write`, `file_edit`, `web_search`, `web_fetch`, the scheduling tools,
`skill_manage`, and `set_working_directory`. **Personal** gets everything.
`shell_execute` is Personal-only — in a Team or Public session, use `file_list`
to enumerate a directory instead of `ls`.

Tools belonging to disabled subsystems (see [Feature Kill Switches](#feature-kill-switches))
are hidden from `search_tools` results for all audiences. Public sessions
additionally cannot discover or load skills, subagents, memory tools,
scheduling tools, or the `web_search` / `web_fetch` tools regardless of
feature flags.

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

Approvals are typed `(verb, directory)` pairs in `tool-approvals.json`:

- **verb** — the command head plus subcommand chain only (e.g. `git push`,
  `grep`, `freshdesk`). No flags, no path arguments.
- **directory** — the directory the grant applies to. Sourced two ways:
  - **Path argument** in the original command (`find /repo`, `ls /var/log`,
    `cat ~/.bashrc`). The path argument is the directory; for file targets
    the parent directory is used so `cat ~/.bashrc` scopes to `~`.
  - **Cwd** when no path argument is present (`git status`, `freshdesk`).
  - **`null`** for the global wildcard ("approve this verb in any
    directory") — only set by `Always anywhere`.

**Folder-scoped trust compounds.** An entry on `(find, /home/user/repo)`
auto-allows `find /home/user/repo/.netclaw -name X` because the candidate's
extracted path is under the entry's directory. You don't have to call
`set_working_directory` for this — running a command with a path argument
declares scope implicitly.

The approval gate runs three layers in order:

1. **Hard-deny list** — system-protected paths. Always blocks.
2. **Safe-verb ∩ safe-space short-circuit** — when the verb is on the curated
   safe list AND the effective directory (path arg or cwd) is under your
   declared safe space (`session_dir` or `project_dir`), the call auto-runs
   with no prompt. The list covers demonstrably read-only verbs: file readers
   (`ls`, `grep`, `cat`, …), system/info verbs (`date`, `whoami`, `uname`,
   `uptime`, …), and read-only `git`/`gh` queries (`git status`, `git log`,
   `gh pr view`, `gh run list`, …). Mutating verbs (`git push`, `git fetch`,
   `rm`, `sed -i`), command-prefixing verbs (`env`, `xargs`, `sudo`),
   network-writing verbs (`gh api`, `curl`), and environment/process-inspection
   verbs (`printenv`, `ps`) are never on the list — the safe-space gate
   cannot scope a verb that dumps the environment or the process table.
3. **Interactive prompt** — everything else. Five buttons:
   - **Once** — run this one time, persist nothing.
   - **This chat** — allow the verbs in this directory for the rest of the
     session.
   - **Always here** — persist `(verb, effective directory)`. The
     "directory" is the command's path argument when present, else cwd.
   - **Always anywhere** — persist `(verb, null)` global wildcard.
     Danger style.
   - **Deny** — refuse this call only.

**Side-effect-only clauses are authorized but not persisted.** When a
compound command includes pure side-effect verbs (`echo`, `printf`, `:`,
`true`, `false`) with no path argument and no redirect, those clauses are
authorized for the current call by the click but no `ApprovalEntry` is
written for them. Recording every literal `echo "==="` would be noise.

**Prompts survive passivation and restart.** Pending approval prompts are
journaled with their requester and trust context, so if the session goes idle or
the daemon restarts before the user clicks, the click is still honored when it
arrives. Completed sibling tool results are journaled per call, so recovery
re-drives unresolved calls rather than replaying the whole batch. The only case
where a click does nothing is a genuinely expired prompt (the turn already
failed or was superseded); the session then posts a visible "approval prompt has
expired" notice rather than silently dropping the click. If a user reports a
stale button, ask them to re-issue the request.

**Why you may not see a prompt at all.** If the user invokes a read-only verb
(say `grep`) with a path argument under a tree the operator has previously
trusted, the safe-verb short-circuit applies and there is no prompt. This
is intended behavior — read-only inspection of declared work surfaces is
implicit. Mutating verbs in the same directory still prompt.

**When the prompt offers fewer buttons.** Two cases:

- **Complex commands** (bash control-flow like `for/while/done`, unbalanced
  quotes/brackets) get only `Once` and `Deny`. The matcher cannot extract a
  clean verb chain to remember, so persistence is structurally impossible.
- **Shallow cwd** (e.g. `/etc/`, `/`) hides `Always here` only. Persisting a
  too-shallow root would grant the verb across most of the filesystem;
  `This chat` and `Always anywhere` remain available.

If a user keeps getting prompted in their repo on read-only verbs, the
likely cause is the commands they're running don't carry a path argument
(e.g. `git status` with no `-C`). Suggest they call
`set_working_directory <path>` so the safe-verb short-circuit treats that
tree as a safe space. If they keep getting prompted for the same mutating
verb (e.g. `git push`), suggest `Always here` to persist
`(git push, effective directory)`.

When auditing repeated prompts, check both the tool audit trail and daemon
logs. A later call satisfied by an existing grant records
`ApprovalDecision=PreviouslyApproved` and an `ApprovalPattern` like
`git push [persistent: git push in /home/user/repo]`. If the daemon prompts
despite a same-verb persisted grant, it logs an approval near-miss with the
candidate directory, cwd, persisted grant, creation time, and mismatch reason.

### Inspecting, revoking, and pre-approving grants

Use the `netclaw approvals` CLI rather than hand-editing
`tool-approvals.json`. The daemon reads the file on every approval check, so
mutations take effect on the next prompt without a daemon restart.

```bash
# Interactive TUI: see everything grouped by audience and tool
netclaw approvals

# List — human-readable. Entries print as "<verb> in <dir>" or "<verb> anywhere",
# each followed by when the grant was added ("added 3 days ago"; "added —" for
# grants saved before timestamps were tracked).
netclaw approvals list
netclaw approvals list --audience personal --tool shell_execute

# Scriptable JSON output (audiences → tools → typed entries)
netclaw approvals list --json

# Revoke by user-visible form (the same labels list emits)
netclaw approvals revoke "git remote in /home/user/repos/foo/"
netclaw approvals revoke "freshdesk anywhere"

# Pre-approve a verb as a global wildcard for unattended/scheduled tasks
netclaw approvals trust-verb freshdesk
netclaw approvals trust-verb gh --audience team

# Clear every entry for a tool (optionally scoped to one audience)
netclaw approvals revoke --tool shell_execute --all
netclaw approvals revoke --tool shell_execute --all --audience personal
```

`revoke` of a non-existent pattern exits non-zero with a clear message — the
CLI never silently succeeds. `trust-verb` is idempotent — re-running it on an
existing entry exits zero with "no changes."

### Pre-approving for unattended tasks (load-bearing)

Reminders and webhooks fire without a human present and cannot answer prompts.
When you (the agent) are helping the user set up an unattended task that needs
shell commands, **identify the verbs the task will need and proactively suggest
pre-approving them as global wildcards** before the schedule fires.

Example dialogue when the user asks you to schedule a daily Freshdesk report:

> "I'll set up a daily reminder that calls `freshdesk --since=24h`. Since
> reminders run unattended and can't prompt for approval, I need to pre-approve
> the `freshdesk` verb globally — that's a `(freshdesk, null)` entry, meaning
> it will auto-allow in any cwd. Mind if I do that with
> `netclaw approvals trust-verb freshdesk`?"

On confirmation, run the trust-verb command via `shell_execute`, then create
the reminder. The grant persists across daemon restarts.

### Last-resort recovery

If the approval file gets corrupted (the daemon will quarantine it to
`tool-approvals.json.invalid` and warn loudly), or if a v1 store gets detected
during upgrade (the daemon quarantines it to `tool-approvals.json.v1.bak`),
the active file is reset and the v2 store starts empty.

To wipe every persistent grant and start clean, delete the file directly:

macOS/Linux:

```bash
rm ~/.netclaw/config/tool-approvals.json
```

PowerShell:

```powershell
Remove-Item "$HOME/.netclaw/config/tool-approvals.json" -Force
```

Restart the daemon so in-memory session approvals are cleared too.

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

`set_webhook` inherits the audience of the channel/session that created it when
`audience` is omitted — the same provenance model as reminders. A route cannot be
minted with a broader audience than the creator holds; downgrading is always
allowed. A webhook created from a Team channel runs as Team (and therefore cannot
run `shell_execute`); one created from a Personal CLI session runs as Personal.

Route files are secret-bearing config because they may contain inline
verification secrets. Treat `config/webhooks` like `secrets.json` and avoid
broad file reads/writes there unless the user explicitly wants raw config work.

Verification kinds are generic:

- `Hmac`
- `HeaderSecret`

Route files hot-reload without restarting the daemon. If a route file becomes
invalid, Netclaw removes that route immediately and emits an operational alert.

**Approval gate:** Webhooks run without a human — they cannot prompt for
approval. The same rules as reminders apply: shell commands must be pre-approved
in `tool-approvals.json`, and path arguments are scoped by the route's audience
the same way `file_write` is. See "Approval Requirements for Reminders and
Webhooks" in the Scheduling section.

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

When a user sends a file in Slack, Discord, or Mattermost, Netclaw runs the
attachment through an ingress pipeline before it reaches the LLM:

1. **Policy gate** — uses declared MIME plus filename extension for a provisional
   catalog-backed category, then checks audience and per-message file count
   against `ChannelAttachmentPolicy`
2. **Size gate** — rejects files above the per-audience byte limit
3. **Download** — fetches from Slack's private file API with bot-token auth
4. **Content scan** — runs the configured `IContentScanner` and produces a
   scanner-verified canonical MIME type
5. **Inbox write** — saves to `~/.netclaw/sessions/{session-id}/inbox/`
6. **Announcement line** — appends `[attachment]` text to the user turn

The `[attachment]` line format is:
```
[attachment] name="..." mime="..." size=N path="inbox/..." inlined="true|false"
```

`inlined="true"` means the file bytes were forwarded to the model as
`DataContent` (currently image files on image-capable models). `inlined="false"`
means the model only sees the path reference.

Declared transport MIME is metadata, not proof. Attachment announcements,
inlined `DataContent`, and model-input handoff use the scanner-verified MIME.
Unknown image/audio/video subtypes do not get privileged categories by prefix;
they must be explicitly present in the media catalog. OpenAI-compatible
providers only serialize image `DataContent` through `image_url` and fail loudly
if non-image bytes reach that boundary.

`file_read` follows the same file taxonomy as chat attachments. It reads
text-like files directly, including UTF-8, UTF-16/UTF-32 Unicode text, and
common Windows-1252 text files. For images, it can load the file for visual
inspection when the active model or delegated sub-agent supports image input.
For PDFs, audio/video, archives, binary documents, and unknown binaries, it
returns metadata plus explicit guidance; it does not perform PDF extraction,
OCR, transcription, keyframe extraction, or raw binary output.

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

## Secret Management

Secrets live in `~/.netclaw/config/secrets.json`; never print raw values in a
conversation, issue, PR, or log summary. Use the CLI instead of direct edits:

```bash
netclaw secrets set Discord:BotToken <replacement>
netclaw secrets set Slack.BotToken <replacement>
```

Rules:

- `.` and `:` are both accepted as path delimiters; prefer the documented dotted
  form unless the operator provides a configuration-style colon path.
- `netclaw secrets add` is an alias for `set` and overwrites the same effective
  path.
- Re-running `netclaw init` on an existing install opens an action menu
  (`Redo identity setup`, `Open configuration editor`, `Start over from
  scratch`, `Cancel`) rather than re-walking setup. Update individual secrets
  with `netclaw secrets set` or the relevant `netclaw config` editor.
- If a channel reports a 401 or invalid-token error, rotate the relevant secret
  and restart the daemon so the channel reloads config.

## LLM Providers

Netclaw routes chat completions through configured **provider entries** in
`secrets.json` / `netclaw.json`. Each entry has a logical name (operator-chosen)
and a `type` (well-known identifier). Manage them with `netclaw provider`:

| Subcommand | Purpose |
|------------|---------|
| `netclaw provider list` | Show configured entries and their types |
| `netclaw provider add <name> <type> [...flags]` | Add a new entry |
| `netclaw provider remove <name>` | Delete an entry |
| `netclaw provider rename <old> <new>` | Rename without re-authenticating |
| `netclaw provider` (no args) | Interactive TUI for add/edit/delete |

### Supported provider types

| Type | Auth | Notes |
|------|------|-------|
| `ollama` | Endpoint only | `--endpoint http://host:11434` |
| `openai` | API key **or** OAuth (ChatGPT sub) | Codex backend for OAuth path |
| `openai-compatible` | API key + endpoint | Generic OpenAI-shape proxies |
| `anthropic` | API key | `sk-ant-...` |
| `openrouter` | API key | `sk-or-...` |
| `github-copilot` | OAuth device flow only | Requires active Copilot subscription on the GitHub account |
| `veniceai` | API key | OpenAI-compatible at `https://api.venice.ai/api/v1`. Suppresses Venice's prepended system prompt by default; opt in via `VendorOptions.IncludeVeniceSystemPrompt = true` |

Provider-specific behavior toggles belong under
`Providers.<name>.VendorOptions`. Netclaw keeps that bag opaque at the core
config layer; each provider plugin deserializes and validates its own typed
options instead of adding provider-specific properties to `ProviderEntry`.

For OpenAI ChatGPT subscription auth, Netclaw persists the OAuth access token,
refresh token, and ChatGPT account ID returned by the OpenAI ID token. The
account ID is required by the Codex backend. If OpenAI OAuth validation reports
that the account ID is missing, re-authenticate the provider with `netclaw
provider fix <name>` or remove and add it again. API-key OpenAI auth does not
use the Codex backend or this account-ID metadata.

For `openai` OAuth providers, `netclaw model discover <provider>` queries the
Codex backend model catalog with the OAuth bearer token and
`ChatGPT-Account-Id`. This path is fail-closed: if the live catalog is
unavailable, returns no picker-visible models, or omits context-window or
input-modality metadata, Netclaw reports the provider error instead of using a
stale built-in model list. The catalog query's `client_version` tracks the
official `@openai/codex` release version, not Netclaw's own version, because the
Codex backend uses that value to gate newer model entries.

When adding an OpenAI provider from the CLI, `netclaw provider add <name>
openai` defaults to the ChatGPT OAuth device flow. Use `--auth api-key
--api-key <key>` to force platform API-key auth instead.

### Adding GitHub Copilot

GitHub Copilot uses the OAuth device flow only — no API key. The operator
must have an active personal Copilot subscription. From the CLI:

```bash
netclaw provider add my-copilot github-copilot --auth oauth-device
```

The terminal prints a user code and the URL `https://github.com/login/device`.
The operator opens the URL in a browser, enters the code, and approves the
Netclaw GitHub App. On success, the long-lived GitHub OAuth token is
persisted to `secrets.json`. A short-lived (~30 min) Copilot API token is
minted lazily on each chat request and never written to disk.

If a Copilot probe or chat call returns "GitHub Copilot authorization
expired", the stored OAuth token has been revoked. The remediation is:

```bash
netclaw provider remove my-copilot
netclaw provider add my-copilot github-copilot --auth oauth-device
```

The token is **not** auto-cleared on 401 — the operator retains visibility
into the failing credential until they explicitly remove the entry.

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
| Switch update channel (saved) | `netclaw update --channel beta` |
| Self-diagnose | `netclaw doctor` |
| Runtime health | `netclaw status` |
| Memory/token stats | `netclaw stats` |
| Historical skill usage by method/name | `netclaw stats skills` |
| List/manage skills | `netclaw skill list` |
| List past sessions | `netclaw sessions --once` |
| Inspect reminder history | `netclaw reminder history <id> --last 5` |
| Permanently delete a reminder | `netclaw reminder delete <id>` |

## Demo AppHost

For "show me Netclaw working end-to-end" or "I want to kick the tires
without setting up Slack and provider accounts," point the user at the
self-contained .NET Aspire demo under `samples/Netclaw.Demo.AppHost/`.

```text
dotnet run --project samples/Netclaw.Demo.AppHost
```

One command brings up a containerized Mattermost (seeded with admin,
team, bot, access token, default channel, and a test user), a
containerized Ollama with `qwen3.5:2b-q4_K_M` pulled and cached, and the
Netclaw daemon as an Aspire project resource sandboxed via
`NETCLAW_HOME` so nothing touches a host-installed `~/.netclaw/`.
Default credentials and the seeded channel name are printed to the
Aspire dashboard.

Key facts to share with the operator:

- Aspire dashboard at <http://localhost:15294>; Mattermost web UI URL is
  visible there under the `mattermost` resource's `web` endpoint
  (port allocated dynamically).
- Default Mattermost login for the demo's non-admin test user:
  `testuser` / `TestUser1234!`. Admin is `admin` / `Admin1234!`.
- The demo launches the `fast` profile by default. It keeps the seeded
  Mattermost channel on the `public` audience, caps tool loops
  aggressively, disables Ollama thinking mode, tunes Ollama for
  single-user local inference, and prewarms the model before the
  daemon starts.
- For the heavier tool-rich path, opt into
  `NETCLAW_DEMO_PROFILE=full dotnet run --project samples/Netclaw.Demo.AppHost`.
- Daemon binds `127.0.0.1:5299` (not the production default 5199, so
  it never collides with a host-installed daemon).
- `fast` is materially quicker on CPU than the old demo path, but GPU is
  still the best experience; the README documents the
  `WithGPUSupport(OllamaGpuVendor.Nvidia)` opt-in for snappy demos.
- Clean reset: `rm -rf samples/Netclaw.Demo.AppHost/.demo-home/` plus
  `docker volume rm` for the Ollama volume.

The demo is for evaluation, not production. It uses `mattermost-preview`
(deprecated upstream but self-contained), runs the daemon as a host
process (containerizing collides with `ExposureMode.Local` + loopback
auth — see the README's "Why the daemon isn't containerized" section),
and ships with no custom `netclaw.json` so the default secure posture
applies.

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
