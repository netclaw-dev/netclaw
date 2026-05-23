# TUI-002: `netclaw config` Wireframes

Source PRDs: `PRD-004`, `PRD-001`, `PRD-002`

Backing OpenSpec change: `openspec/changes/netclaw-config-command/`

Companion: `TUI-001-command-wireframes.md` (init wizard + chat + plain CLI),
`TUI-003-simplified-init-wireframes.md` (the trimmed init flow that ships
alongside `netclaw config`).

## Overview

`netclaw config` is a menu-driven Termina TUI command for live configuration
editing. Operators reach every editable section without leaving the terminal,
without re-entering existing secrets, and without hand-editing
`netclaw.json`. Each section editor is reentrant by construction (pre-fills
non-secret fields from on-disk state) and doctor-blessed on save (relevant
checks run against the candidate config before write).

Twelve editors ship day one:

| Editor                  | SectionId                    | Category        | Multi-value |
|-------------------------|------------------------------|-----------------|-------------|
| Search Provider         | `Search`                     | —               | no          |
| Slack Channels          | `Slack`                      | Chat Channels   | partial     |
| Discord Channels        | `Discord`                    | Chat Channels   | partial     |
| Mattermost Channels     | `Mattermost`                 | Chat Channels   | partial     |
| Exposure Mode           | `Daemon.ExposureMode`        | —               | partial     |
| Security Posture        | `Security.Posture`           | —               | no          |
| Audience Profiles       | `Tools.AudienceProfiles`     | —               | partial     |
| Outbound Webhooks       | `Notifications.Webhooks`     | —               | yes         |
| Inbound Webhooks        | `Webhooks`                   | —               | no          |
| External Skill Dirs     | `ExternalSkills`             | —               | yes         |
| Skill Feeds             | `SkillFeeds`                 | —               | yes         |
| Browser Automation      | `BrowserAutomation`          | —               | no          |

## Termina Component Vocabulary

All wireframes reference Termina 0.5.1 components (same as TUI-001):

- **PanelNode** — bordered container with optional title
- **TextInputNode** — single or multi-line text input (masked variant for secrets)
- **SelectionListNode** — keyboard-navigable option list (single or multi-select)
- **TextNode** — static or dynamic text block
- **SpinnerNode** — animated progress indicator (used for Test Connection actions)

## Conventions

### Status glyph vocabulary

| Glyph | Meaning |
|-------|---------|
| `✓`   | Section configured, all relevant doctor checks pass |
| `⚠`   | Section configured, at least one check returns WARN |
| `✗`   | Section configured, at least one check returns ERROR (blocks save) |
| `–`   | Section unset / default / disabled |
| `▸`   | Currently focused row |

A footer hint on the dashboard reads:
`✓ ok · ⚠ warning · ✗ error · – not set`

### Keystroke conventions

| Key             | Effect                                                                |
|-----------------|-----------------------------------------------------------------------|
| `↑` / `↓`       | Move focus within list                                                |
| `←` / `→`       | Move focus across action row (Save / Cancel / etc.)                   |
| `Tab` / `Shift+Tab` | Move focus across fields in a form                                |
| `Enter`         | Activate focused element (open editor, submit, toggle)                |
| `Esc`           | Cancel / go back. Confirms discard if section has unsaved changes.    |
| `d`             | In list editors: delete focused item (with inline `[y/N]` confirm)    |
| `q`             | Dashboard quit only                                                   |
| `Space`         | Toggle focused checkbox                                               |

### Footer hint style

Every page renders a single-line footer at the bottom listing the relevant
keystrokes for that page. Page-specific. Common combinations defined in the
page templates below.

### Title bar conventions

Every page has a single-line title bar at top, framed by the panel border:

```
╭─ <Page title> ───────────────────────────────...
```

Sub-pages use a breadcrumb form:

```
╭─ Outbound Webhooks › Edit "critical-pager" ──...
```

---

## Navigation tree

```
netclaw config
  └── Config.0  Dashboard  ◀─ all editors return here on Save/Cancel
        ├── Config.1   Search Provider
        ├── Config.2   Slack Channels
        ├── Config.3   Discord Channels
        ├── Config.4   Mattermost Channels
        ├── Config.5   Exposure Mode
        ├── Config.6   Security Posture
        ├── Config.7   Audience Profiles            ← addresses #1150
        ├── Config.8   Outbound Webhooks
        ├── Config.9   Inbound Webhooks
        ├── Config.10  External Skill Directories
        ├── Config.11  Skill Feeds
        ├── Config.12  Browser Automation
        ├── Config.D   Run full doctor
        └── Quit

netclaw config  (when no netclaw.json exists)
  └── Config.E0  Refuse with `netclaw init` pointer ─── exit non-zero
```

---

## Page templates

Reusable patterns referenced by the per-editor sections below.

### T1. Single-value editor (no secret, no sub-pages)

```
╭─ <Section Title> ───────────────────────────────────────────╮
│                                                             │
│  <Field 1 label>:                                           │
│  <input or selector>                                        │
│                                                             │
│  <Field N label>:                                           │
│  <input or selector>                                        │
│                                                             │
│  [ Save ]    [ Cancel ]                                     │
│                                                             │
│ Tab next · Enter activate · Esc cancel                      │
╰─────────────────────────────────────────────────────────────╯
```

Transitions:
- `Tab` cycles fields.
- `Enter` on Save → run blessing → write or block.
- `Enter` or `Esc` on Cancel → discard-confirm (T7) if dirty → return to dashboard.

### T2. Multi-value list with inline edits

```
╭─ <Section Title> ───────────────────────────────────────────╮
│                                                             │
│  ▸ <item 1 display>                                         │
│    <item 2 display>                                         │
│    <item 3 display>                                         │
│                                                             │
│    + Add <item-noun>                                        │
│                                                             │
│  [ Save ]    [ Cancel ]                                     │
│                                                             │
│ ↑/↓ navigate · Enter edit · d remove · Esc cancel           │
╰─────────────────────────────────────────────────────────────╯
```

Transitions:
- `Enter` on an item → inline edit overlay (single-line input).
- `Enter` on `+ Add` → inline empty input overlay.
- `d` on an item → inline `Remove? [y/N]` prompt; `y` removes, anything else cancels.
- `Enter` on Save → write list to schema array → return to dashboard.
- `Esc` on Cancel → discard-confirm if dirty.

### T3. Multi-value list with sub-page items

Same as T2 visually. `Enter` on item or `+ Add` opens a sub-page (T4)
instead of inline edit.

```
╭─ <Section Title> ───────────────────────────────────────────╮
│                                                             │
│  ▸ <item 1 name>          <item 1 status>                   │
│    <item 2 name>          <item 2 status>                   │
│                                                             │
│    + Add <item-noun>                                        │
│                                                             │
│  [ Save ]    [ Cancel ]                                     │
│                                                             │
│ ↑/↓ navigate · Enter edit · d remove · Esc cancel           │
╰─────────────────────────────────────────────────────────────╯
```

### T4. Item sub-page (form)

```
╭─ <Parent Title> › <Edit Mode> ──────────────────────────────╮
│                                                             │
│  <Field 1>:                                                 │
│  <input>                                                    │
│                                                             │
│  <Field N>:                                                 │
│  <input>                                                    │
│                                                             │
│  [ Save ]    [ Cancel ]    [ Delete <item-noun> ]           │
│                                                             │
│ Tab next · Enter activate · Esc cancel                      │
╰─────────────────────────────────────────────────────────────╯
```

`Delete` button shown only on Edit mode, not Add. Activating it → T5 with
destructive copy.

Transitions:
- `Save` returns to the parent list with the new/updated item applied to
  in-memory state. Disk write happens on the parent's outer `Save`.
- `Cancel` returns to parent list without applying.
- `Delete` opens T5; on confirm, removes from in-memory list, returns to
  parent.

### T5. Confirmation dialog (default-Cancel)

```
╭─ <Confirm prompt> ──────────────────────────────────────────╮
│                                                             │
│  <Explanation, 1-3 lines>                                   │
│                                                             │
│  ▸ [ Cancel ]    [ Yes, <action> ]                          │
│                                                             │
│ Default: Cancel (Esc or Enter)                              │
╰─────────────────────────────────────────────────────────────╯
```

Default focus on Cancel. `Enter` or `Esc` cancels. `Tab` + `Enter` on
"Yes" confirms.

### T6. Inline validation banner

Rendered above the action row of any editor while doctor blessing finds
issues. ERROR variant:

```
│  ╭─ Issues ───────────────────────────────────────────────╮ │
│  │ ✗ Brave backend requires an API key                    │ │
│  │ ⚠ Endpoint TLS certificate expires in 14 days          │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  [ Save ]  (disabled)   [ Cancel ]                          │
```

WARN-only variant:

```
│  ╭─ Warnings ─────────────────────────────────────────────╮ │
│  │ ⚠ Endpoint TLS certificate expires in 14 days          │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  [ Save anyway ]   [ Cancel ]                               │
```

### T7. Unsaved-changes discard confirm

```
╭─ Discard changes? ──────────────────────────────────────────╮
│                                                             │
│  You have unsaved changes in this section.                  │
│  Closing now will lose them.                                │
│                                                             │
│  ▸ [ Keep editing ]    [ Discard ]                          │
│                                                             │
│ Default: Keep editing (Esc or Enter)                        │
╰─────────────────────────────────────────────────────────────╯
```

Shown when user hits Esc on a section editor with dirty state.

### T8. Empty list placeholder

```
╭─ <Section Title> ───────────────────────────────────────────╮
│                                                             │
│   (no <item-noun> configured)                               │
│                                                             │
│  ▸ + Add <item-noun>                                        │
│                                                             │
│  [ Save ]    [ Cancel ]                                     │
│                                                             │
│ Enter add · Esc cancel                                      │
╰─────────────────────────────────────────────────────────────╯
```

Shown when a list editor opens with zero items.

---

## Config.0 — Dashboard

```
╭─ Netclaw Configuration ─────────────────────────────────────╮
│                                                             │
│ ▸ Search Provider           ✓ Brave                         │
│   Chat Channels                                             │
│     Slack                   ✓ 3 channels, 2 users           │
│     Discord                 – not configured                │
│     Mattermost              – not configured                │
│   Exposure Mode             ✓ Local                         │
│   Security Posture          ✓ Personal                      │
│   Audience Profiles         ✓ default                       │
│   Outbound Webhooks         ⚠ 2 configured, 1 unreachable   │
│   Inbound Webhooks          – disabled                      │
│   External Skill Dirs       ✓ 2 directories                 │
│   Skill Feeds               – none                          │
│   Browser Automation        – disabled                      │
│                                                             │
│   ──────────                                                │
│   Run full doctor                                           │
│   Quit                                                      │
│                                                             │
│ ↑/↓ navigate · Enter open · q quit · ✓ ok · ⚠ warn · ✗ err  │
╰─────────────────────────────────────────────────────────────╯
```

**Status computation:** on dashboard entry, each editor's
`GetStatus(currentConfig)` runs (with `RelevantDoctorChecks` against
on-disk state). Results cached for the dashboard session; re-computed
when returning from a saved editor.

**Sub-grouping indentation:** chat-channel rows render at +2 indent under
the "Chat Channels" label. The label itself is unselectable.

**No "Save dashboard" action:** the dashboard is purely a navigation
layer. All saves are at section granularity.

### Layout structure

```
PanelNode (outer: "Netclaw Configuration")
├── SelectionListNode (single-select; entries from SectionEditorRegistry
│                       grouped by Category, plus "Run full doctor" and
│                       "Quit" tail items)
└── TextNode (footer hint line)
```

---

## Config.E0 — No-config refusal

Rendered when `~/.netclaw/config/netclaw.json` is missing at launch.

```
╭─ No Netclaw configuration found ────────────────────────────╮
│                                                             │
│  No configuration file at:                                  │
│    ~/.netclaw/config/netclaw.json                           │
│                                                             │
│  Run `netclaw init` to create one.                          │
│                                                             │
│  [ OK ]                                                     │
│                                                             │
│ Enter exit                                                  │
╰─────────────────────────────────────────────────────────────╯
```

Non-interactive (when stdout is not a TTY, e.g. CI): prints
`No configuration found. Run \`netclaw init\` first.` to stderr and exits
non-zero. The interactive variant exits zero after acknowledgement.

---

## Config.1 — Search Provider

### 1.1 Main editor

```
╭─ Search Provider ───────────────────────────────────────────╮
│                                                             │
│  Backend:                                                   │
│    ▸ Brave (current)                                        │
│      DuckDuckGo                                             │
│      SearXng (self-hosted)                                  │
│                                                             │
│  Brave API key:                                             │
│  ╭────────────────────────────────────────────────────────╮ │
│  │                                                        │ │
│  ╰────────────────────────────────────────────────────────╯ │
│  (configured — leave blank to keep)                         │
│                                                             │
│  SearXng instance URL:                                      │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ (not applicable — only required for SearXng)           │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  [ Save ]    [ Cancel ]    [ Remove credential ]            │
│                                                             │
│ Tab next · Enter activate · Esc cancel                      │
╰─────────────────────────────────────────────────────────────╯
```

**Field conditionality:** Brave API key disabled when backend ≠ Brave;
SearXng URL disabled when backend ≠ SearXng; DuckDuckGo has no fields.

**Reentrancy:** Backend selector pre-fills from current config. API key
field is empty regardless; hint indicates "configured" or "not set"
based on `ConfigFileHelper.SecretPresent(...)`.

### 1.2 Remove credential confirm (T5)

```
╭─ Remove Brave API key? ─────────────────────────────────────╮
│                                                             │
│  This deletes your Brave API key from secrets.json.         │
│  Search will fall back to DuckDuckGo unless you set a new   │
│  key. You can re-enter at any time.                         │
│                                                             │
│  ▸ [ Cancel ]    [ Yes, remove ]                            │
│                                                             │
│ Default: Cancel (Esc or Enter)                              │
╰─────────────────────────────────────────────────────────────╯
```

**Doctor checks** (`RelevantDoctorChecks`): `ConfigSchemaDoctorCheck`,
`SearchBackendDoctorCheck`.

---

## Config.2 — Slack Channels

### 2.1 Main editor

```
╭─ Slack Channels ────────────────────────────────────────────╮
│                                                             │
│  Bot token:                                                 │
│  ╭────────────────────────────────────────────────────────╮ │
│  │                                                        │ │
│  ╰────────────────────────────────────────────────────────╯ │
│  (configured — leave blank to keep)                         │
│                                                             │
│  App token (Socket Mode):                                   │
│  ╭────────────────────────────────────────────────────────╮ │
│  │                                                        │ │
│  ╰────────────────────────────────────────────────────────╯ │
│  (configured — leave blank to keep)                         │
│                                                             │
│  Allowed channels:           3 configured  →                │
│  Allowed users:              2 configured  →                │
│  DMs enabled:                [ X ] yes                      │
│  Audience profile:           Personal                       │
│                                                             │
│  [ Save ]    [ Cancel ]    [ Test connection ]              │
│  [ Remove credentials ]                                     │
│                                                             │
│ Tab next · Enter activate · Esc cancel                      │
╰─────────────────────────────────────────────────────────────╯
```

Sub-pages:
- "Allowed channels" → 2.2 list editor.
- "Allowed users" → 2.3 list editor.

### 2.2 Allowed channels list (T2)

```
╭─ Slack Channels › Allowed channel IDs ──────────────────────╮
│                                                             │
│  ▸ C01ABCDE                                                 │
│    C01FGHIJ                                                 │
│    C01KLMNO                                                 │
│                                                             │
│    + Add channel ID                                         │
│                                                             │
│  [ Save ]    [ Cancel ]                                     │
│                                                             │
│ ↑/↓ navigate · Enter edit · d remove · Esc cancel           │
╰─────────────────────────────────────────────────────────────╯
```

`Save` here is "apply to in-memory state and return to 2.1." Disk write
happens when 2.1 itself saves.

### 2.3 Allowed users list

Same shape as 2.2 with user IDs. Uses `IdentifierItemEditor`.

### 2.4 Test connection (inline banner)

Runs the existing Slack probe logic from `SlackStepViewModel`; result
rendered in an inline banner above the action row:

```
│  ╭─ Connection test ──────────────────────────────────────╮ │
│  │ ✓ Bot token valid (workspace: petabridge)              │ │
│  │ ✓ Socket Mode app token valid                          │ │
│  │ ✓ Bot has access to 3 of 3 configured channels         │ │
│  ╰────────────────────────────────────────────────────────╯ │
```

Failure shape:

```
│  ╭─ Connection test ──────────────────────────────────────╮ │
│  │ ✗ Bot token invalid: 401 invalid_auth                  │ │
│  │   Check `xoxb-` token in the Slack app config          │ │
│  ╰────────────────────────────────────────────────────────╯ │
```

Test results never modify config; they're advisory before Save.

### 2.5 Remove credentials confirm (T5)

```
╭─ Remove Slack credentials? ─────────────────────────────────╮
│                                                             │
│  This deletes both the Slack bot token and the Socket       │
│  Mode app token from secrets.json. Slack will be            │
│  disconnected until you re-enter both. Allowed channels     │
│  and users are preserved in netclaw.json.                   │
│                                                             │
│  ▸ [ Cancel ]    [ Yes, remove ]                            │
│                                                             │
│ Default: Cancel (Esc or Enter)                              │
╰─────────────────────────────────────────────────────────────╯
```

**Doctor checks:** `ConfigSchemaDoctorCheck`, `SlackAuthDoctorCheck`,
`SlackAclDoctorCheck`.

---

## Config.3 — Discord Channels

Structurally identical to 2.x except:
- Single token field (bot token only; no app token).
- Otherwise: allowed channels list, allowed users list, DMs toggle,
  audience profile, test connection, remove credentials.

(Layouts identical to 2.1–2.5 with the App token row removed.)

**Doctor checks:** `ConfigSchemaDoctorCheck`, `DiscordAuthDoctorCheck`.

---

## Config.4 — Mattermost Channels

Structurally identical to 2.x plus:
- `Server URL` text field at the top.
- Same token, channels, users, DMs, audience profile, test connection,
  remove credentials.

```
╭─ Mattermost Channels ───────────────────────────────────────╮
│                                                             │
│  Server URL:                                                │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ https://chat.example.com                               │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  Bot token:                                                 │
│  ╭────────────────────────────────────────────────────────╮ │
│  │                                                        │ │
│  ╰────────────────────────────────────────────────────────╯ │
│  (configured — leave blank to keep)                         │
│                                                             │
│  Allowed channels:           5 configured  →                │
│  Allowed users:              3 configured  →                │
│  DMs enabled:                [ X ] yes                      │
│  Audience profile:           Team                           │
│                                                             │
│  [ Save ]    [ Cancel ]    [ Test connection ]              │
│  [ Remove credentials ]                                     │
│                                                             │
│ Tab next · Enter activate · Esc cancel                      │
╰─────────────────────────────────────────────────────────────╯
```

**Doctor checks:** `ConfigSchemaDoctorCheck`, `MattermostAuthDoctorCheck`.

---

## Config.5 — Exposure Mode

### 5.1 Mode selection

```
╭─ Exposure Mode ─────────────────────────────────────────────╮
│                                                             │
│  How is Netclaw reachable from outside the host?            │
│                                                             │
│  ▸ Local                                                    │
│    127.0.0.1 only. No external exposure.                    │
│                                                             │
│    Reverse Proxy                                            │
│    Behind nginx/Caddy/etc. Trusted proxies required.        │
│                                                             │
│    Tailscale                                                │
│    Auth via Tailscale identity. Mesh network required.      │
│                                                             │
│    Cloudflare Tunnel                                        │
│    Cloudflare access-protected. Tunnel credentials needed.  │
│                                                             │
│  ──────                                                     │
│  Daemon host:    127.0.0.1                                  │
│  Daemon port:    5199                                       │
│                                                             │
│  [ Configure mode →  ]   [ Save ]   [ Cancel ]              │
│                                                             │
│ ↑/↓ navigate · Tab to buttons · Enter activate              │
╰─────────────────────────────────────────────────────────────╯
```

**Conditionality:** "Configure mode →" button is enabled only when
selected mode requires sub-config (Reverse Proxy, Tailscale, Cloudflare).
Local has no sub-config.

### 5.2 Reverse Proxy sub-form (T1-shaped)

```
╭─ Exposure Mode › Reverse Proxy ─────────────────────────────╮
│                                                             │
│  External base URL (must be HTTPS):                         │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ https://netclaw.example.com                            │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  Trusted proxies (CIDR list):    2 configured  →            │
│                                                             │
│  [ Apply ]    [ Cancel ]                                    │
│                                                             │
│ Tab next · Enter activate · Esc cancel                      │
╰─────────────────────────────────────────────────────────────╯
```

Trusted proxies row → 5.5 list editor.

### 5.3 Tailscale sub-form

```
╭─ Exposure Mode › Tailscale ─────────────────────────────────╮
│                                                             │
│  Tailscale auth key:                                        │
│  ╭────────────────────────────────────────────────────────╮ │
│  │                                                        │ │
│  ╰────────────────────────────────────────────────────────╯ │
│  (configured — leave blank to keep)                         │
│                                                             │
│  Hostname on tailnet:    netclaw                            │
│                                                             │
│  [ Apply ]    [ Cancel ]    [ Remove auth key ]             │
│                                                             │
╰─────────────────────────────────────────────────────────────╯
```

### 5.4 Cloudflare Tunnel sub-form

```
╭─ Exposure Mode › Cloudflare Tunnel ─────────────────────────╮
│                                                             │
│  Tunnel token:                                              │
│  ╭────────────────────────────────────────────────────────╮ │
│  │                                                        │ │
│  ╰────────────────────────────────────────────────────────╯ │
│  (configured — leave blank to keep)                         │
│                                                             │
│  Access policy email domain (optional):                     │
│  ╭────────────────────────────────────────────────────────╮ │
│  │                                                        │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  [ Apply ]    [ Cancel ]    [ Remove tunnel token ]         │
│                                                             │
╰─────────────────────────────────────────────────────────────╯
```

### 5.5 Trusted proxies list (T2 with `IdentifierItemEditor`)

```
╭─ Exposure Mode › Trusted Proxies ───────────────────────────╮
│                                                             │
│  ▸ 10.0.0.0/8                                               │
│    192.168.1.0/24                                           │
│                                                             │
│    + Add CIDR                                               │
│                                                             │
│  [ Save ]    [ Cancel ]                                     │
│                                                             │
│ ↑/↓ navigate · Enter edit · d remove · Esc cancel           │
╰─────────────────────────────────────────────────────────────╯
```

**Doctor checks:** `ConfigSchemaDoctorCheck`, `ExposureModeDoctorCheck`.

---

## Config.6 — Security Posture

### 6.1 Posture selection (T1-shaped)

```
╭─ Security Posture ──────────────────────────────────────────╮
│                                                             │
│  Current posture: Personal                                  │
│                                                             │
│  ▸ Personal                                                 │
│    Just me. Local-only by default. Tools have wide access.  │
│                                                             │
│    Team                                                     │
│    Small team via Slack/Discord. Audience-restricted tools. │
│                                                             │
│    Enterprise                                               │
│    Production deployment. Strict audience profiles, audit.  │
│                                                             │
│  [ Save ]    [ Cancel ]                                     │
│                                                             │
│ ↑/↓ navigate · Tab to buttons · Enter activate              │
╰─────────────────────────────────────────────────────────────╯
```

### 6.2 Cascade warning (T5 variant — three options)

Shown only when changing posture AND `Tools.AudienceProfiles` has been
customized away from the prior posture's defaults.

```
╭─ Posture change affects Audience Profiles ──────────────────╮
│                                                             │
│  You have customized Audience Profiles. Changing posture    │
│  will overwrite them with the new posture's defaults.       │
│                                                             │
│  ▸ [ Cancel — keep current posture ]                        │
│    [ Apply new posture, overwrite profiles ]                │
│    [ Apply new posture, keep custom profiles ]              │
│                                                             │
│ Default: Cancel (Esc or Enter)                              │
╰─────────────────────────────────────────────────────────────╯
```

**Doctor checks:** `ConfigSchemaDoctorCheck`, `SecurityPolicyDoctorCheck`.

---

## Config.7 — Audience Profiles *(addresses #1150)*

### 7.1 Audience selection

```
╭─ Audience Profiles ─────────────────────────────────────────╮
│                                                             │
│  Configure tool access per audience tier.                   │
│                                                             │
│  ▸ Personal           ✓ Default for posture: Personal       │
│    Team               ✓ Default for posture: Personal       │
│    Public             ✓ Default for posture: Personal       │
│                                                             │
│  ──────                                                     │
│                                                             │
│  Shell mode (global): HostAllowed                           │
│                                                             │
│  [ Cancel ]                                                 │
│                                                             │
│ ↑/↓ navigate · Enter edit audience · Esc cancel             │
╰─────────────────────────────────────────────────────────────╯
```

### 7.2 Per-audience editor

```
╭─ Audience Profiles › Team ──────────────────────────────────╮
│                                                             │
│  Tools enabled for the Team audience:                       │
│                                                             │
│  [ X ] memory                                               │
│  [ X ] search                                               │
│  [ X ] skills                                               │
│  [   ] scheduling                                           │
│  [ X ] sub-agents                                           │
│  [   ] webhooks                                             │
│                                                             │
│  Shell mode for Team:    SandboxOnly                        │
│  Approval policy:        Required                           │
│                                                             │
│  [ Save ]    [ Cancel ]    [ Reset to posture default ]     │
│                                                             │
│ ↑/↓ navigate · Space toggle · Tab to buttons · Esc cancel   │
╰─────────────────────────────────────────────────────────────╯
```

**Key bindings critical to #1150:**

- `↑` / `↓` MUST move focus between toggle rows.
- `Space` MUST toggle the focused checkbox.
- `Enter` on a checkbox row also toggles (alternative to Space).
- `Tab` moves to the action row.
- `Reset to posture default` replaces all toggles + shell mode with the
  posture-default mapping.

The `config-audience.tape` smoke tape explicitly exercises `↓`, `Space`,
`↑`, `Space` to lock in the keystroke contract. Regression in arrow
nav OR toggle is caught.

**Doctor checks:** `ConfigSchemaDoctorCheck`, `ToolAudienceProfilesDoctorCheck`.

---

## Config.8 — Outbound Webhooks

### 8.1 List page (T3)

```
╭─ Outbound Webhooks ─────────────────────────────────────────╮
│                                                             │
│  ▸ ops-alerts            ✓ healthy                          │
│    critical-pager        ⚠ unreachable last 3 attempts      │
│                                                             │
│    + Add webhook                                            │
│                                                             │
│  [ Save ]    [ Cancel ]                                     │
│                                                             │
│ ↑/↓ navigate · Enter edit · d remove · Esc cancel           │
╰─────────────────────────────────────────────────────────────╯
```

Empty-state (T8):

```
╭─ Outbound Webhooks ─────────────────────────────────────────╮
│                                                             │
│   (no webhooks configured)                                  │
│                                                             │
│  ▸ + Add webhook                                            │
│                                                             │
│  [ Save ]    [ Cancel ]                                     │
│                                                             │
│ Enter add · Esc cancel                                      │
╰─────────────────────────────────────────────────────────────╯
```

### 8.2 Add/edit form (T4)

```
╭─ Outbound Webhooks › Edit "critical-pager" ─────────────────╮
│                                                             │
│  Name:                                                      │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ critical-pager                                         │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  URL:                                                       │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ https://events.pagerduty.com/v2/enqueue                │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  Auth header (e.g. "Authorization: Bearer ..."):            │
│  ╭────────────────────────────────────────────────────────╮ │
│  │                                                        │ │
│  ╰────────────────────────────────────────────────────────╯ │
│  (configured — leave blank to keep)                         │
│                                                             │
│  Event filter (optional, comma-separated):                  │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ session.error,session.compaction                       │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  [ Save ]    [ Cancel ]    [ Delete webhook ]               │
│                                                             │
│ Tab next · Enter activate · Esc cancel                      │
╰─────────────────────────────────────────────────────────────╯
```

### 8.3 Delete confirm (T5)

```
╭─ Remove webhook "critical-pager"? ──────────────────────────╮
│                                                             │
│  This webhook will be removed from Notifications.Webhooks.  │
│  Any stored auth header for it will be deleted.             │
│                                                             │
│  ▸ [ Cancel ]    [ Yes, remove ]                            │
│                                                             │
│ Default: Cancel (Esc or Enter)                              │
╰─────────────────────────────────────────────────────────────╯
```

**Doctor checks:** `ConfigSchemaDoctorCheck`, `WebhookFormatDoctorCheck`.

---

## Config.9 — Inbound Webhooks

```
╭─ Inbound Webhooks ──────────────────────────────────────────╮
│                                                             │
│  Inbound webhooks let external systems trigger Netclaw      │
│  via signed HTTP requests. Routes are defined per webhook   │
│  under ~/.netclaw/config/webhooks/*.json (file-edited).     │
│                                                             │
│  [ X ] Inbound webhooks enabled                             │
│                                                             │
│  Request timeout (seconds): 30                              │
│                                                             │
│  [ Save ]    [ Cancel ]                                     │
│                                                             │
│ Tab next · Space toggle · Enter activate · Esc cancel       │
╰─────────────────────────────────────────────────────────────╯
```

**Note:** route file editing remains file-based; this editor only
toggles the feature and sets the timeout. If user enables this flag
but no routes exist, `InboundWebhookRoutesDoctorCheck` (existing)
surfaces the empty-routes condition — per CLAUDE.md "fail loudly,"
we do NOT silently default to dummy routes.

**Doctor checks:** `ConfigSchemaDoctorCheck`, `InboundWebhookRoutesDoctorCheck`.

---

## Config.10 — External Skill Directories

### 10.1 List page (T2 with `PathItemEditor`)

```
╭─ External Skill Directories ────────────────────────────────╮
│                                                             │
│  ▸ ~/.claude/skills                                         │
│    ~/work/team-skills                                       │
│    ~/personal-skills                                        │
│                                                             │
│    + Add directory                                          │
│                                                             │
│  [ Save ]    [ Cancel ]                                     │
│                                                             │
│ ↑/↓ navigate · Enter edit · d remove · Esc cancel           │
╰─────────────────────────────────────────────────────────────╯
```

Empty state per T8.

### 10.2 Inline add/edit overlay

```
│    ~/work/team-skills                                       │
│  ╭─ Edit directory ───────────────────────────────────────╮ │
│  │ ~/personal-skills_                                     │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  [Enter] save  ·  [Esc] cancel                              │
```

Renders as an overlay row replacing the focused item. Validates: path
exists, is a directory, is readable. Errors render inline below the
input row.

### 10.3 Inline delete confirm

When `d` pressed on a focused item:

```
│  ▸ ~/.claude/skills        Remove? [y/N]                    │
```

Single-keypress. `y` removes; anything else cancels. No modal.

**Doctor checks:** `ConfigSchemaDoctorCheck`, `ExternalSkillSourcesDoctorCheck`.

---

## Config.11 — Skill Feeds

### 11.1 List page (T3 with `SkillFeedItemEditor`)

```
╭─ Skill Feeds ───────────────────────────────────────────────╮
│                                                             │
│  ▸ corp-internal-feed       ✓ reachable                     │
│    legacy-feed              ✗ 403 forbidden                 │
│                                                             │
│    + Add feed                                               │
│                                                             │
│  [ Save ]    [ Cancel ]                                     │
│                                                             │
│ ↑/↓ navigate · Enter edit · d remove · Esc cancel           │
╰─────────────────────────────────────────────────────────────╯
```

### 11.2 Add/edit form (T4)

```
╭─ Skill Feeds › Edit "corp-internal-feed" ───────────────────╮
│                                                             │
│  Name:                                                      │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ corp-internal-feed                                     │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  Feed URL:                                                  │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ https://skills.internal.corp/manifest.json             │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  API key (Bearer token, optional):                          │
│  ╭────────────────────────────────────────────────────────╮ │
│  │                                                        │ │
│  ╰────────────────────────────────────────────────────────╯ │
│  (configured — leave blank to keep)                         │
│                                                             │
│  [ Save ]    [ Cancel ]    [ Test connection ]              │
│  [ Delete feed ]                                            │
│                                                             │
│ Tab next · Enter activate · Esc cancel                      │
╰─────────────────────────────────────────────────────────────╯
```

### 11.3 Delete confirm (T5)

```
╭─ Remove feed "legacy-feed"? ────────────────────────────────╮
│                                                             │
│  This feed will be removed from SkillFeeds.Feeds. Any       │
│  stored Bearer token for it will be deleted.                │
│                                                             │
│  ▸ [ Cancel ]    [ Yes, remove ]                            │
│                                                             │
│ Default: Cancel (Esc or Enter)                              │
╰─────────────────────────────────────────────────────────────╯
```

**Doctor checks:** `ConfigSchemaDoctorCheck`, `SkillFeedsDoctorCheck`
(WARN-only — transient outages don't block saves).

---

## Config.12 — Browser Automation

### 12.1 Status & toggle (Playwright not installed)

```
╭─ Browser Automation ────────────────────────────────────────╮
│                                                             │
│  Headless browser support via Playwright. Used by the       │
│  `browser` tool for web scraping and form interaction.      │
│                                                             │
│  Status: Playwright not installed                           │
│                                                             │
│  [   ] Browser automation enabled                           │
│  (cannot enable until Playwright is installed)              │
│                                                             │
│  [ Install instructions →  ]    [ Cancel ]                  │
│                                                             │
│ Tab next · Enter activate · Esc cancel                      │
╰─────────────────────────────────────────────────────────────╯
```

### 12.2 Status & toggle (Playwright installed)

```
╭─ Browser Automation ────────────────────────────────────────╮
│                                                             │
│  Status: Playwright installed (v1.42.0)                     │
│                                                             │
│  [ X ] Browser automation enabled                           │
│                                                             │
│  [ Save ]    [ Cancel ]    [ Uninstall instructions →  ]    │
│                                                             │
│ Tab next · Space toggle · Enter activate · Esc cancel       │
╰─────────────────────────────────────────────────────────────╯
```

### 12.3 Install instructions sub-page

```
╭─ Browser Automation › Install Playwright ───────────────────╮
│                                                             │
│  Playwright is not currently installed. To install:         │
│                                                             │
│    1. Run:                                                  │
│         dotnet tool install --global Microsoft.Playwright.CLI│
│                                                             │
│    2. Then:                                                 │
│         playwright install chromium                         │
│                                                             │
│  After installation, return to this editor and re-open to   │
│  detect the installation.                                   │
│                                                             │
│  [ OK ]                                                     │
│                                                             │
│ Enter exit                                                  │
╰─────────────────────────────────────────────────────────────╯
```

**Why not shell out to install:** installing global tooling from a TUI
is too magical and platform-fragile. Print instructions; let the user
run them in their shell. Detection on re-open is automatic
(`BrowserAutomationDoctorCheck` resolves `playwright` from PATH at
editor entry).

**Doctor checks:** `ConfigSchemaDoctorCheck`, `BrowserAutomationDoctorCheck`.

---

## Config.D — Run full doctor

```
╭─ Doctor — full configuration check ─────────────────────────╮
│                                                             │
│  ✓ ConfigSchema             OK                              │
│  ✓ Providers                OK                              │
│  ✓ Models                   OK                              │
│  ⚠ Search                   Brave API key valid but rate-   │
│                              limited per recent probes       │
│  ✓ Slack                    OK                              │
│  – Discord                  Not configured                  │
│  – Mattermost               Not configured                  │
│  ✓ Exposure                 OK (Local)                      │
│  ✓ AudienceProfiles         OK                              │
│  ✗ Notifications.Webhooks   critical-pager unreachable      │
│  ✓ ExternalSkills           OK                              │
│  – SkillFeeds               None configured                 │
│  – BrowserAutomation        Disabled                        │
│                                                             │
│  Summary: 8 pass · 1 warning · 1 error · 4 skipped          │
│                                                             │
│  Exit code on close: 1 (errors present)                     │
│                                                             │
│  [ Back to dashboard ]                                      │
│                                                             │
│ Enter back · Esc back                                       │
╰─────────────────────────────────────────────────────────────╯
```

Invokes the same `DoctorRunner` used by `netclaw doctor`. Results page
renders status per check.

---

## Daemon-restart nudge at exit

Printed to stderr after Termina teardown when (a) at least one section
saved during the session AND (b) the daemon is currently running.

```
Config saved. Restart the daemon to apply changes:
  netclaw daemon stop && netclaw daemon start
```

When the daemon is not running OR no saves occurred, the nudge is
omitted.

**Daemon detection:** `netclaw config` uses the same lightweight probe
as `netclaw daemon status` (PID file lookup at the documented path,
falling back to a port-open check on the configured daemon port). The
probe is bounded to 250 ms; if the probe times out, the nudge is
omitted (conservative — better to miss the nudge than to falsely
suggest a restart).
