---
name: netclaw-operations
description: "REQUIRED when the user asks about scheduling, reminders, cron jobs, timers, background jobs, diagnostics, troubleshooting, MCP tools, daemon health, identity updates, or Netclaw capabilities and self-maintenance."
metadata:
  author: netclaw
  version: "2.28.0"
---

# Netclaw Operations

This is your operational guide. Load it when the user's request is about
Netclaw itself — what it can do, how to schedule work, how to diagnose
problems, how to update preferences, or how to maintain itself.

## Route by Intent

Safety-critical and high-frequency guidance (tool arguments, large output,
approvals, identity-vs-memory routing) is inline below. Everything else lives in
a reference file — load the one matching the user's intent with
`skill_read_resource`.

| User intent | Where |
|-------------|-------|
| Schedule reminders/cron; run background shell jobs | `skill_read_resource('netclaw-operations', 'references/scheduling.md')` |
| How tool arguments are validated | [Tool argument validation](#tool-argument-validation) |
| Handle very large tool output | [Large tool output](#large-tool-output) |
| Understand approval prompts | [Approval Prompts](#approval-prompts) |
| Update identity / where facts go (identity vs memory) | [Identity](#identity) |
| Work on a project, switch projects | `skill_read_resource('netclaw-operations', 'references/projects.md')` |
| Discover MCP / available tools | `skill_read_resource('netclaw-operations', 'references/tools.md')` |
| Manage skills and sources | `skill_read_resource('netclaw-operations', 'references/skills.md')` |
| Manage inbound webhooks / attachments | `skill_read_resource('netclaw-operations', 'references/webhooks.md')` |
| Add/switch LLM or search provider, OAuth login | `skill_read_resource('netclaw-operations', 'references/providers.md')` |
| Diagnose problems, kill switches, self-update | `skill_read_resource('netclaw-operations', 'references/diagnostics.md')` |
| Rotate or repair secrets | `skill_read_resource('netclaw-operations', 'references/secrets.md')` |
| Pair remote devices, manage access | `skill_read_resource('netclaw-operations', 'references/devices.md')` |
| Kick the tires on Netclaw end-to-end locally | `skill_read_resource('netclaw-operations', 'references/demo-apphost.md')` |

## Project Directory

`set_working_directory(path)` sets the session's project root (absolute path within
allowed roots); the project's identity file (`.netclaw/AGENTS.md`, `CLAUDE.md`,
`AGENTS.md`, or `CONTEXT.md`) then loads into the prompt. Full rules:
`skill_read_resource('netclaw-operations', 'references/projects.md')`.

For Team and Personal sessions, `[working-context]` is refreshed at the start
of each new turn. In a Git project it includes the active worktree, branch,
HEAD, upstream divergence, and dirty counts. Treat this as turn-start
grounding: a checkout or commit performed during the current tool loop appears
in the next turn's snapshot. Subagents receive a read-only project/recent-file
snapshot and return confirmed file edits to the parent when they complete.

## Scheduling & Background Jobs

Reminders: `set_reminder` with schedule type `once` / `interval` / `cron`. Always
set `delivery_kind` explicitly (`current_session` / `channel` / `none`). A reminder
that fires unattended cannot answer approval prompts, so pre-approve any shell verbs
it needs first with `netclaw approvals trust-verb <verb>`. Background shell: set
`_background: true` on `shell_execute` (max 5 concurrent; cancel servers/watchers
when done; background jobs are killed when the session passivates).

Full detail — delivery contract, proactive channel messaging, approval scoping,
job lifecycle — is in
`skill_read_resource('netclaw-operations', 'references/scheduling.md')`.

## Tool argument validation

Prefer the canonical argument names exactly as a tool declares them, and the
canonical meta keys `_rationale`, `_timeout_seconds`, `_background` (leading
underscore, snake_case). Recognition is spelling-tolerant so a near-miss is
consumed rather than dropped: declared params fold case/punctuation, and the
meta keys also accept the underscore-dropped/cased/shortened forms
(`TimeoutSeconds`, `timeout_seconds`, `Timeout` → the timeout hint; `Rationale`,
`Background` likewise). The supplied value is always *used* — never silently
defaulted.

Three things are still rejected loudly, and when rejected the tool did NOT run —
fix and re-issue once, do not retry the same shape:

- **Unknown keys** — a key that matches no parameter and no meta field rejects
  with a `did you mean '<canonical>'?` suggestion and the list of valid names.
- **Invalid values** — a value that cannot parse as its type (`_timeout_seconds:
  "1200ms"`, `_background: "yes"`) rejects instead of falling back to a default.
- **Ambiguous meta spelling** — supplying two keys that map to the same meta
  field (e.g. both `_timeout_seconds` and `TimeoutSeconds`) rejects; send one.

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

Only a core toolset is always loaded. Use `search_tools(query)` to find additional
or MCP tools by capability before concluding a tool doesn't exist. Full guidance:
`skill_read_resource('netclaw-operations', 'references/tools.md')`.

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

Skills load on demand; manage skills and sources via the skill tools. Full guidance:
`skill_read_resource('netclaw-operations', 'references/skills.md')`.

## Webhooks & Inbound Attachments

Inbound webhooks are configured per route; route files are secret-bearing and
protected. Attachment handling is covered alongside. Full setup + rules:
`skill_read_resource('netclaw-operations', 'references/webhooks.md')`.

## Secret Management

Secrets live in `~/.netclaw/config/secrets.json` — **never print raw secret values**
in chat, issues, PRs, or logs. Set them via CLI (`netclaw secrets set <Path> <value>`),
never by direct file edit. Protected paths (`secrets.json`, `.netclaw/keys`,
`config/webhooks`) are always access-denied. Full rotation guidance:
`skill_read_resource('netclaw-operations', 'references/secrets.md')`.

## LLM & Search Providers

Add or switch model providers (including OAuth login) and configure search backends
(e.g. SearXNG) via provider config. Full setup:
`skill_read_resource('netclaw-operations', 'references/providers.md')`.

## Diagnostics, Kill Switches & Self-Maintenance

When something is broken, start with `netclaw status`, then `netclaw doctor`. Feature
kill switches and self-update/health are covered in the reference. Memory embeddings
can be backfilled with `netclaw memory backfill-embeddings [--force]`; doctor checks
memory embedding availability. Full guidance:
`skill_read_resource('netclaw-operations', 'references/diagnostics.md')`.

## Identity

Your identity is defined by layered files loaded into the session prompt:

| Layer | Source | Audience |
|-------|--------|----------|
| SOUL.md | `~/.netclaw/identity/SOUL.md` (filesystem) | All |
| AGENTS.md | Embedded in the Netclaw binary (audience-specific) | Team/Personal get full version; Public gets stripped version |
| TOOLING.md | `~/.netclaw/identity/TOOLING.md` (filesystem) | Team/Personal only |
| Project instructions | `.netclaw/AGENTS.md` etc. in project directory | Team/Personal only |

**AGENTS.md is binary-owned.** The full AGENTS (Team/Personal) contains operating
rules, autonomy guidance, grounding, search policy, scheduling, background shell,
subagent delegation, skill reference, identity file paths, and memory triage. The
Public AGENTS contains only basic operating rules, autonomy, grounding, and media
attachment guidance.

SOUL.md and TOOLING.md remain editable on disk:
- To edit: read the file first with `file_read`, then write with `file_write`.
- Detail subdirectories: `identity/soul/`, `identity/tooling/`.

**Identity vs memory — what goes where:**

- **Identity files define the agent**: persona, tone, communication style, operating
  rules, and the foundational user grounding set at init (the user's name, timezone).
  Edit these only to change *how the agent itself operates*.
- **Durable facts and preferences about the user** learned or stated over time
  (favorite things, family, history, working preferences) → **memory**
  (`store_memory`); they are recalled when relevant. A user asking you to "remember"
  a preference is a memory write, **not** a SOUL.md edit.

When unsure: does it change who the agent *is* or how it operates? → identity file.
Is it a fact about the user to recall later? → memory.

## Device Pairing

Pair remote devices and manage their access via the pairing flow. Full steps:
`skill_read_resource('netclaw-operations', 'references/devices.md')`.

## Demo AppHost

To demo or kick the tires on Netclaw end-to-end locally:
`skill_read_resource('netclaw-operations', 'references/demo-apphost.md')`.
