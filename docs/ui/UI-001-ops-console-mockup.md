# UI-001: Ops Console Mockup

Source PRDs: `PRD-003`, `PRD-002`, `PRD-004`

## Visual Direction

Theme: "Control Room"

- deep slate background with copper accent status indicators
- compact mono-friendly typography for diagnostics
- clear severity channels (info, warn, critical)
- no decorative-only panels; every panel carries operational signal

## Global Layout

```
+--------------------------------------------------------------------------------+
| NETCLAW OPS CONSOLE                    env:pi1   mode:single-process   v0.1.0 |
+----------------------+---------------------------------------------------------+
| Navigation           | Overview                                                |
| - Overview           | +------------------+ +-----------------+ +-----------+ |
| - Sessions           | | Gateway Health   | | Slack           | | Policy    | |
| - Policy             | | HEALTHY          | | CONNECTED       | | DENIES 4h| |
| - Security           | +------------------+ +-----------------+ +-----------+ |
| - Diagnostics        |                                                         |
|                      | +-----------------------------------------------------+ |
| Quick Actions        | | Active Sessions                                     | |
| [Validate Config]    | | channel/general/1739403120.0012   turns: 44        | |
| [Run Doctor]         | | channel/lab/1739409000.5555       turns: 12        | |
| [Inspect ACL]        | +-----------------------------------------------------+ |
+----------------------+---------------------------------------------------------+
```

## Screen Specifications

## 1) Overview

Primary cards:

- gateway health and uptime
- Slack socket connection and reconnect metrics
- persistence (journal/snapshot) status
- policy deny count in rolling windows

Activity feed:

- latest policy denies
- latest compaction events
- actor recovery failures

## 2) Sessions List

Columns:

- Session ID (`channelId/threadTs`)
- Last Activity
- Turns
- Compacted (yes/no)
- Last Snapshot Sequence
- Status (active/recovering/faulted)

Filters:

- channel
- fault state
- compaction state

## 3) Session Inspector

Panels:

- Turn timeline (user/assistant/system)
- Snapshot and event metadata
- Policy evaluation history for inbound turns
- manual actions: compact now, replay diagnostics

## 4) Policy Editor

Workflow:

1. Load active ACL JSON.
2. Edit with schema-aware validation.
3. Run simulation (`sender/channel/tool` cases).
4. Apply with confirmation and audit note.

## 5) Security Page

Displays:

- bind mode: loopback/public
- transport mode: Slack Socket Mode
- pairing pending count
- privileged approval queue
- warnings for risky exposure combinations

## 6) Diagnostics

Tabs:

- gateway
- actor system
- persistence
- policy engine

Each tab supports copyable remediation snippets.

## 6A) Tools (MCP)

Route: `/tools`

Panels:

- MCP server list with health badges
- discovered tools per server
- last invocation failures with reason and timestamp

## 7) Onboarding Wizard

Route: `/onboarding`

Steps:

1. Environment check
2. Slack Socket Mode setup
3. Model provider setup (OpenRouter default)
4. ACL bootstrap
5. Exposure mode selection (local, tailscale, cloudflare)
6. Final validation and launch checklist

Wizard requirements:

- resumable progress
- secret-safe inputs
- inline validation before next step
- explicit warnings for public exposure modes

## Component States

- All critical status badges include text label, icon, and timestamp.
- Empty states include a concrete next action.
- Failure states include direct CLI command equivalent.

## 8) Memory and Configuration

Route: `/memory-config`

```
+--------------------------------------------------------------------------------+
| NETCLAW OPS CONSOLE                                           Memory & Config  |
+----------------------+---------------------------------------------------------+
| Navigation           | ╭─ Personality ───────────────────────────────────────╮ |
| - Overview           | │ PERSONALITY.md  │ INSTRUCTIONS.md │ USER.md        │ |
| - Sessions           | │ [View] [Edit]   │ [View] [Edit]   │ [View] [Edit]  │ |
| - Policy             | ╰────────────────────────────────────────────────────╯ |
| - Security           |                                                         |
| - Diagnostics        | ╭─ Projects ──────────────────────────────────────────╮ |
| - Memory & Config  * | │ Name       Path                     Lang   CI       │ |
| - Scheduling         | │ netclaw    /home/user/repos/netclaw  C#     ✓       │ |
|                      | │ termina    /home/user/repos/termina  C#     ✓       │ |
| Quick Actions        | │ [Add Project]                                       │ |
| [Validate Config]    | ╰────────────────────────────────────────────────────╯ |
| [Run Doctor]         |                                                         |
| [Inspect ACL]        | ╭─ Environment ───────────────────────────────────────╮ |
|                      | │ git 2.43  gh 2.62  dotnet 10.0  node 22.1          │ |
|                      | │ Last scan: 2026-02-21 14:30                         │ |
|                      | │ [Rescan]                                             │ |
|                      | ╰────────────────────────────────────────────────────╯ |
+----------------------+---------------------------------------------------------+
```

Panels:

- **Personality** — view and edit soul files (PERSONALITY.md, INSTRUCTIONS.md,
  USER.md). Inline markdown editor with preview.
- **Projects** — registered project list with path, language, CI status.
  Add/remove projects. Links to project AGENTS.md when present.
- **Environment** — discovered tools and versions from last environment scan.
  Rescan button triggers a fresh discovery.

## 9) Scheduling

Route: `/scheduling`

```
+--------------------------------------------------------------------------------+
| NETCLAW OPS CONSOLE                                              Scheduling    |
+----------------------+---------------------------------------------------------+
| Navigation           | ╭─ Scheduled Tasks ───────────────────────────────────╮ |
| - Overview           | │ Name          Type     Schedule     Status   Next   │ |
| - Sessions           | │ daily-standup  cron     0 9 * * 1-5  active   09:00 │ |
| - Policy             | │ check-ci       interval 30m          active   14:32 │ |
| - Security           | │ backup-db      cron     0 2 * * *    paused   --    │ |
| - Diagnostics        | │                                                     │ |
| - Memory & Config    | │ [Create Task] [Pause All]                           │ |
| - Scheduling       * | ╰────────────────────────────────────────────────────╯ |
|                      |                                                         |
| Quick Actions        | ╭─ Execution History ─────────────────────────────────╮ |
| [Validate Config]    | │ Time           Task          Duration  Result       │ |
| [Run Doctor]         | │ 14:02 today    check-ci      12s       ✓ success   │ |
| [Inspect ACL]        | │ 09:00 today    daily-standup  45s       ✓ success   │ |
|                      | │ 02:00 today    backup-db      --        ⊘ paused    │ |
|                      | │ 14:02 yest.    check-ci      8s        ✓ success   │ |
|                      | ╰────────────────────────────────────────────────────╯ |
+----------------------+---------------------------------------------------------+
```

Panels:

- **Scheduled Tasks** — task list with name, type (cron/interval), schedule
  expression, status (active/paused), next fire time. Actions: create, pause,
  resume, delete. Pause All for maintenance windows.
- **Execution History** — recent execution log with timestamp, task name,
  duration, and result. Click-through to session inspector for full details.

## CLI Parity Matrix

- Overview health <-> `netclaw gateway status`
- Policy simulator <-> `netclaw acl test` / `netclaw acl explain`
- Diagnostics panel <-> `netclaw gateway doctor`
- Session inspector <-> `netclaw session inspect`
- Memory & Config <-> `netclaw memory show` / `netclaw project list` / `netclaw environment show`
- Scheduling <-> `netclaw schedule list` / `netclaw schedule show`
