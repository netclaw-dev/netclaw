# TUI-002: `netclaw config` Wireframes

Source PRDs: `PRD-004`, `PRD-001`, `PRD-002`

Backing OpenSpec change: `openspec/changes/netclaw-config-command/`

Companion: `TUI-001-command-wireframes.md` (init wizard + chat + plain CLI),
`TUI-003-simplified-init-wireframes.md` (the trimmed init flow that ships
alongside `netclaw config`).

## Overview

`netclaw config` is a menu-driven Termina TUI command for post-install
configuration. The root is domain-oriented and navigation-first rather than a
flat list of every editable leaf. Operators reach the high-churn settings
surfaces without leaving the terminal, without re-entering existing secrets,
and without hand-editing `netclaw.json`.

Leaf editors remain reentrant by construction and validate before persistence.
Completed inline actions autosave; typed drafts and multi-field forms persist
only when explicitly applied. The root dashboard groups editors by operator
intent and has no save action of its own.

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

| Key             | Effect                                                                 |
|-----------------|------------------------------------------------------------------------|
| `↑` / `↓`       | Move focus within a list or row editor                                  |
| `←` / `→`       | Change a focused cycle value; if the change is complete, autosave        |
| `Tab` / `Shift+Tab` | Move focus across fields in a multi-field form                     |
| `Enter`         | Activate focused element; `Apply` accepts a draft/form and validates     |
| `Esc`           | Go back, or cancel an incomplete draft/input without persisting it        |
| `Delete`        | Remove focused item when the footer exposes remove semantics             |
| `Ctrl+Q`        | Quit the TUI from any page                                              |
| `Space`         | Toggle focused checkbox; if the change is complete, autosave             |

### Autosave interaction contract

`netclaw config` uses completed-action autosave for inline editors. There is no
root save action and ordinary leaf editors SHOULD NOT expose a separate `Save`
row when the operator has already completed an action.

Rules:

- Completed actions autosave immediately after validation. Examples: toggling a
  feature, changing an audience cycle, adding/removing a channel, applying
  allowed users, applying rotated credentials, changing a backend preference, or
  confirming reset.
- `Apply` means "accept this typed draft or multi-field form, then validate and
  autosave." It is not a separate staged save button.
- `Done` means "leave this task/context." It never writes by itself. It is used
  when the operator benefits from an explicit finish affordance even though
  completed edits are already saved.
- `Esc` navigates back or cancels incomplete input only. It never persists
  edits.
- Failed validation leaves persisted files unchanged. If a toggle or cycle value
  cannot be saved, the visible state rolls back to the last persisted value.
- Writes are section-preserving and field-scoped: a Channels edit must not wipe
  unrelated providers; a Browser Automation edit must not rewrite unrelated MCP
  profiles; secret fields preserve existing secrets when left blank.

Footer wording:

- Use `Toggle/Save` only for a focused toggle that writes immediately.
- Use `Apply` for typed drafts and multi-field forms that write after Enter.
- Use `Done` for navigation-only finish rows.
- Use `Back`, `Menu`, `Channels`, or `Settings Areas` to name the actual return
  destination.

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
  └── Config.0  Domain dashboard
        ├── Config.1   Inference Providers  ──→ routes to `netclaw provider`
        ├── Config.2   Models               ──→ routes to `netclaw model`
        ├── Config.3   Channels
        │     ├── Slack
        │     ├── Discord
        │     └── Mattermost
        ├── Config.4   Inbound Webhooks
        ├── Config.5   Skill Sources
        ├── Config.6   Search
        ├── Config.7   Browser Automation
        ├── Config.8   Telemetry & Alerting
        ├── Config.9   Security & Access
        │     ├── Security Posture
        │     ├── Enabled Features
        │     ├── Audience Profiles            ← addresses #1150
        │     └── Exposure Mode
        └── Quit

netclaw config  (when no netclaw.json exists)
  └── prints refusal to stderr and exits non-zero
```

---

## Page templates

Reusable patterns referenced by the per-editor sections below.

### T1. Single-value inline editor

```
╭─ <Section Title> ───────────────────────────────────────────╮
│                                                             │
│  <Explanation of what this setting controls.>               │
│                                                             │
│  Current: <current value>                                   │
│  New:     <typed draft or selected value>                   │
│                                                             │
│  <Helper copy, only if useful.>                             │
│                                                             │
│ Type/Paste edit · Backspace delete · Enter apply · Esc back │
╰─────────────────────────────────────────────────────────────╯
```

Transitions:
- Typing changes draft state only.
- `Enter` validates and writes the accepted draft.
- `Esc` returns without persisting an incomplete draft.
- Success/failure is shown in the status line.

### T2. Multi-value list with action rows

```
╭─ <Section Title> ───────────────────────────────────────────╮
│                                                             │
│  ▸ <item 1 display>                         [◀ Value ▶]    │
│    <item 2 display>                                         │
│    <item 3 display>                                         │
│                                                             │
│    + Add <item-noun>                                        │
│    Done <verb phrase>                                       │
│                                                             │
│ ↑/↓ navigate · ←/→ change/save · Enter edit/done · Esc back │
╰─────────────────────────────────────────────────────────────╯
```

Transitions:
- `←` / `→` on an item changes the value and autosaves immediately.
- `Enter` on an item opens the relevant edit sub-flow.
- `Enter` on `+ Add` opens an add draft; accepting the draft autosaves.
- `Enter` on `Done ...` exits the local task/context without writing.
- `Delete` on a removable item removes it and autosaves immediately.
- `Esc` returns to the parent menu.

### T3. Multi-value list with sub-page items

Same as T2 visually. `Enter` on item or `+ Add` opens a sub-page/form (T4)
instead of inline edit.

```
╭─ <Section Title> ───────────────────────────────────────────╮
│                                                             │
│  ▸ <item 1 name>          <item 1 status>                   │
│    <item 2 name>          <item 2 status>                   │
│                                                             │
│    + Add <item-noun>                                        │
│    Back                                                     │
│                                                             │
│ ↑/↓ navigate · Enter open/back · Esc back                   │
╰─────────────────────────────────────────────────────────────╯
```

### T4. Item sub-page or multi-field form

```
╭─ <Parent Title> › <Edit Mode> ──────────────────────────────╮
│                                                             │
│  <Field 1>:                                                 │
│  <input>                                                    │
│                                                             │
│  <Field N>:                                                 │
│  <input>                                                    │
│                                                             │
│  <Existing secret helper, only when applicable.>            │
│                                                             │
│ Tab field · Enter apply · Esc back                          │
╰─────────────────────────────────────────────────────────────╯
```

Transitions:
- `Enter` validates the full draft/form and autosaves.
- Secret fields are blank by default; blank means preserve the stored secret.
- `Esc` returns to parent without persisting incomplete draft input.
- Destructive delete/reset actions use T5 before writing.

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

### T6. Inline validation status

Rendered in the status line, or immediately below the affected row when the
error needs row-local context. ERROR variant:

```
│  Browser Automation cannot be enabled: Playwright missing.  │
```

WARN-only variant:

```
│  Slack channel label lookup failed: rate limited.           │
```

Validation failures block the write and leave persisted files unchanged.
Warnings may leave the already-valid screen open with a yellow status line.

### T7. Incomplete-draft cancel rule

Most config editors do not need a discard-confirm dialog because completed
actions save immediately and incomplete drafts have not been persisted. `Esc`
from a typed draft or form cancels that draft and returns to the parent screen.
Use a discard-confirm dialog only when a future editor intentionally supports a
long-lived staged state that can span multiple completed sub-actions before any
write.

### T8. Empty list placeholder

```
╭─ <Section Title> ───────────────────────────────────────────╮
│                                                             │
│   (no <item-noun> configured)                               │
│                                                             │
│  ▸ + Add <item-noun>                                        │
│    Back                                                     │
│                                                             │
│ Enter add/back · Esc back                                   │
╰─────────────────────────────────────────────────────────────╯
```

Shown when a list editor opens with zero items.

---

## Config.0 — Domain dashboard

```
╭─ Netclaw Configuration ─────────────────────────────────────╮
│                                                             │
│ ▸ Inference Providers      2 configured                     │
│   Models                   3 roles assigned                 │
│   Channels                 2 enabled                        │
│   Inbound Webhooks         – disabled                       │
│   Skill Sources            2 dirs · 1 feed                  │
│   Search                   ✓ Brave                          │
│   Browser Automation       – disabled                       │
│   Telemetry & Alerting     OTLP off · 1 webhook             │
│   Security & Access        Team · 4/6 enabled               │
│                                                             │
│   Quit                                                      │
│                                                             │
│ ↑/↓ navigate · Enter open · q quit · ✓ ok · ⚠ warn · ✗ err  │
╰─────────────────────────────────────────────────────────────╯
```

**Status computation:** each domain row shows a concise aggregate summary of
the underlying leaf editors or routed command state.

**No root save action:** the dashboard is purely a navigation layer. All saves
are at leaf-editor granularity.

### Layout structure

```
PanelNode (outer: "Netclaw Configuration")
├── SelectionListNode (single-select; domain entries plus Quit)
└── TextNode (footer hint line)
```

---

## Config.1 — Inference Providers

Selecting `Inference Providers` hands off to the existing `netclaw provider`
TUI. In this branch, that handoff is one-way: provider manager behavior stays
unchanged and does not grow a config-dashboard back-stack.

## Config.2 — Models

Selecting `Models` hands off to the existing `netclaw model` TUI. Model
manager behavior stays unchanged in this branch.

---

## No-config refusal

When `~/.netclaw/config/netclaw.json` is missing, `netclaw config` does not
start Termina at all. It prints:

`No configuration found. Run \`netclaw init\` first.`

to stderr and exits non-zero.

---

## Config.3 — Channels

### 3.1 Channels picker

```
╭─ Channels ──────────────────────────────────────────────────╮
│                                                             │
│  Which channels would you like to connect?                  │
│                                                             │
│   ▶ [✓] Slack                2 channels, 1 user             │
│     [ ] Discord              disabled, saved setup          │
│     [ ] Mattermost                                         │
│     Done adding channels     Return to Settings Areas       │
│                                                             │
│  ↑/↓ to navigate, Space to toggle, Enter to open selected.  │
│  Select Done when finished; completed changes are already   │
│  saved.                                                     │
│  Unconfigured adapters open first-time setup. Configured    │
│  adapters open management without prompting for credentials.│
│                                                             │
│ ↑/↓ navigate · Space toggle/save · Enter open/done · Esc back│
╰─────────────────────────────────────────────────────────────╯
```

Unconfigured adapters reuse the original `netclaw init` sub-flow visuals:

- Slack: bot token -> Socket Mode app token -> channel names/IDs -> DMs ->
  user access choice -> allowed user IDs when restricted.
- Discord: bot token -> channel IDs -> DMs -> user access choice -> allowed
  user IDs when restricted.
- Mattermost: server URL -> bot token -> channel IDs -> DMs -> user access
  choice -> allowed user IDs when restricted -> optional callback URL.

**Autosave model:** First-time setup sub-flows update in-memory state, then drop
the operator directly into Channels & Permissions so every new channel gets an
explicit audience. Completing setup, toggling an existing adapter, adding or
removing a channel, changing an audience, applying allowed users, applying DM
settings, rotating credentials, and confirming reset all validate and autosave
through the shared config-editor merge pipeline. `Done adding channels` is a
navigation affordance only; it never writes by itself.

**Secret reentrancy:** Configured adapters do not ask for credentials on
normal re-entry. Secret fields are shown only from first-time setup or explicit
Rotate credentials. If a stored secret exists, the field shows
`(configured - leave blank to keep)`. Blank submission preserves the existing
secret; entering a new value replaces it.

**Disabled adapters:** Toggling off a previously configured adapter writes
`<Adapter>.Enabled = false` and preserves dormant channel/user fields plus
stored credentials. The daemon ignores those fields while the adapter is
disabled.

**Validation:** Save blocks missing required credentials for enabled adapters,
invalid Mattermost server URLs, and unresolved channel targets. Slack channel
names entered as `#name` or `name` are resolved through Slack before save and
persisted as Slack channel IDs. Discord and Mattermost channel IDs are checked
with their provider APIs before the config merge is written.

### 3.2 Adapter management menu

```
╭─ Channels ──────────────────────────────────────────────────╮
│                                                             │
│  Slack is configured.                                       │
│  enabled · bot token configured · app token configured ·    │
│  2 channels · 1 user · DMs disabled                         │
│                                                             │
│  What would you like to do?                                 │
│                                                             │
│   ▶ Manage channels and permissions                         │
│     Add a Slack channel                                     │
│     Manage allowed users                                    │
│     Direct messages                                         │
│     Rotate credentials                                      │
│     Disable Slack                                           │
│     Reset Slack connection                                  │
│                                                             │
│ ↑/↓ navigate · Enter select · Esc Channels                  │
╰─────────────────────────────────────────────────────────────╯
```

The same menu is used for Slack, Discord, and Mattermost. Disable/enable only
changes `<Adapter>.Enabled`; dormant channel fields and stored credentials are
preserved. Reset is immediate after confirmation: confirming reset deletes the
adapter config section and its secrets before returning to the picker.

### 3.3 Channels and permissions

```
╭─ Channels ──────────────────────────────────────────────────╮
│                                                             │
│  Slack > Channels & Permissions                             │
│  Configure allowed channels and their audience/trust level. │
│                                                             │
│   ▶ C01                    C01                [◀ Team     ▶]│
│     C02                    C02                [◀ Team     ▶]│
│     Direct messages        dm                 [◀ Personal ▶]│
│     + Add channel                                           │
│     Done adding channels                                    │
│                                                             │
│  Audience controls which tools and data this channel can use│
│                                                             │
│ ↑/↓ navigate · ←/→ audience/save · Enter edit/done · a add  │
│ Delete remove · Esc menu                                    │
╰─────────────────────────────────────────────────────────────╯
```

Channel rows write `<Adapter>.AllowedChannelIds` and
`<Adapter>.ChannelAudiences[channelId]`. The DM row writes
`<Adapter>.AllowDirectMessages` plus `<Adapter>.ChannelAudiences["dm"]`.
Removing a channel removes both the channel ID and its audience mapping. DM
audience is preserved when DMs are disabled so re-enabling DMs restores the
operator's last chosen audience. The `+ Add channel` action opens a typed draft;
accepting it validates and autosaves. `Done adding channels` returns to the
adapter management menu and does not write.

### 3.4 Credentials and reset

```
╭─ Channels ──────────────────────────────────────────────────╮
│                                                             │
│  Slack > Credentials                                        │
│  Secret fields are blank by design. Leave blank to keep     │
│  existing secrets.                                          │
│                                                             │
│  Bot token:                                                 │
│  ╭─ Bot token ────────────────────────────────────────────╮ │
│  │                                                       │ │
│  ╰───────────────────────────────────────────────────────╯ │
│  configured - leave blank to keep                         │
│                                                             │
│ Tab field · Enter apply · Esc menu                         │
╰─────────────────────────────────────────────────────────────╯
```

Slack exposes bot token and Socket Mode app token. Discord exposes bot token.
Mattermost exposes server URL, bot token, and optional callback URL. Blank
secret submissions preserve existing secrets; non-blank secret submissions
replace only that secret. Enter validates the full credential draft and
autosaves only if validation succeeds.

---

## Config.5 — Skill Sources

Skill Sources manages the places Netclaw loads skills from. The UI keeps the
same two concepts that exist in today's `netclaw init` flow:

- **Local folders** — additional skill directories on disk, including detected
  well-known folders from other agent tools and operator-provided team folders.
- **Remote skill servers** — HTTP(S) skill feeds that implement the skill
  discovery protocol.

This surface manages source inventory and source health. Skill feature
enablement remains in Security & Access, and individual skill browse/install
actions remain under `netclaw skill`.

### 5.1 Navigation workflow

```
netclaw config
  └── Skill Sources
      ├── Sources inventory
      │   ├── Add local folder
      │   │   ├── Enter path
      │   │   ├── Choose symlink policy
      │   │   ├── Probe folder + preview discovered skills
      │   │   └── Apply -> autosave -> source detail
      │   ├── Add skill server
      │   │   ├── Enter server URL
      │   │   ├── Choose auth: no auth / bearer token
      │   │   ├── Probe discovery endpoint
      │   │   ├── Confirm source name
      │   │   └── Apply -> autosave -> source detail
      │   ├── Rescan all
      │   ├── Focus source -> source detail
      │   └── Done -> Settings Areas
      └── Source detail
          ├── Toggle enabled
          ├── Test/rescan source
          ├── Rename / change path / change URL / rotate token
          ├── Remove source
          └── Done -> Sources inventory
```

### 5.2 Treatment A — unified source inventory (recommended)

This treatment presents local folders and remote skill servers as one inventory,
grouped by type. It works best when operators care about "where skills come
from" more than about the underlying config section names.

```
╭─ Skill Sources ─────────────────────────────────────────────╮
│                                                             │
│  Places Netclaw loads skills from.                          │
│  Skill enablement stays in Security & Access.               │
│                                                             │
│  Local folders                                               │
│  ▸ ✓ dotnet-skills       ~/.claude/skills        42 skills  │
│    ✓ team-skills         ~/work/team-skills      11 skills  │
│                                                             │
│  Remote skill servers                                        │
│    ✓ company-feed        https://skills.acme.io   18 skills │
│    ⚠ lab-feed            https://lab.example      auth fail │
│                                                             │
│    + Add local folder                                        │
│    + Add skill server                                        │
│    Rescan all                                                │
│    Done                                                     │
│                                                             │
│ ↑/↓ navigate · Enter open/apply · Space toggle enabled      │
│ Delete remove · Esc Settings Areas                          │
╰─────────────────────────────────────────────────────────────╯
```

The inventory never says `ExternalSkills.Sources` or `SkillFeeds.Feeds`. Those
are persistence details. Rows show the source's user-facing name, location, and
last known discovery result.

### 5.3 Treatment B — two-lane landing

This alternate treatment makes the two concepts more explicit up front. It is
clearer for first-time operators but costs one extra click before editing an
individual source.

```
╭─ Skill Sources ─────────────────────────────────────────────╮
│                                                             │
│  Choose the kind of source to manage.                       │
│                                                             │
│  ▸ Local skill folders                                      │
│      2 enabled · 53 skills discovered                       │
│      Folders Netclaw scans from this machine.               │
│                                                             │
│    Remote skill servers                                     │
│      2 configured · 1 warning                               │
│      HTTP(S) feeds that publish skill indexes.              │
│                                                             │
│    Rescan all sources                                       │
│    Done                                                     │
│                                                             │
│ ↑/↓ navigate · Enter select · Esc Settings Areas            │
╰─────────────────────────────────────────────────────────────╯
```

Use Treatment A unless the inventory becomes too dense for narrow terminals.

### 5.4 Local folder detail

```
╭─ Skill Sources › team-skills ───────────────────────────────╮
│                                                             │
│  Type: Local folder                                         │
│  Status: ✓ 11 skills discovered                             │
│                                                             │
│  ▸ Enabled                 [x]                              │
│    Path                    ~/work/team-skills               │
│    Allow symlinks          [ ]                              │
│    Rescan folder                                            │
│    Rename source                                            │
│    Change path                                              │
│    Remove source                                            │
│    Done                                                     │
│                                                             │
│ Space toggle/save · Enter apply/open · Delete remove        │
│ Esc Skill Sources                                           │
╰─────────────────────────────────────────────────────────────╯
```

Changing `Enabled` or `Allow symlinks` autosaves after validation. `Change path`
opens a typed path draft; `Apply` validates that the directory exists before
persisting.

### 5.5 Add local folder flow

```
╭─ Add Local Skill Folder ────────────────────────────────────╮
│                                                             │
│  Folder path                                                │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ ~/work/team-skills                                    │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  This must be an existing local directory.                  │
│                                                             │
│  [ Apply ]    [ Cancel ]                                    │
│                                                             │
│ Enter apply · Esc cancel                                    │
╰─────────────────────────────────────────────────────────────╯
```

```
╭─ Local Folder Security ─────────────────────────────────────╮
│                                                             │
│  Allow symlinks inside this folder?                         │
│                                                             │
│  ▸ No — stricter security                                   │
│    Yes — this folder intentionally uses symlinks            │
│                                                             │
│  Symlinks can make a source scan files outside the folder.  │
│                                                             │
│ ↑/↓ navigate · Enter apply · Esc back                       │
╰─────────────────────────────────────────────────────────────╯
```

```
╭─ Review Local Folder ───────────────────────────────────────╮
│                                                             │
│  ✓ Folder is readable                                       │
│  ✓ 11 skills discovered                                     │
│                                                             │
│  Source name                                                │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ team-skills                                            │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  [ Add source ]    [ Back ]    [ Cancel ]                   │
│                                                             │
│ Enter apply/autosave · Esc cancel                           │
╰─────────────────────────────────────────────────────────────╯
```

### 5.6 Remote skill server detail

```
╭─ Skill Sources › company-feed ──────────────────────────────╮
│                                                             │
│  Type: Remote skill server                                  │
│  Status: ✓ connected · 18 skills discovered                 │
│                                                             │
│  ▸ Enabled                 [x]                              │
│    URL                     https://skills.acme.io           │
│    Authentication          bearer token configured          │
│    Sync interval           60 minutes                       │
│    Test connection                                          │
│    Rename source                                            │
│    Change URL                                                │
│    Rotate token                                              │
│    Remove token                                              │
│    Remove source                                             │
│    Done                                                     │
│                                                             │
│ Space toggle/save · Enter apply/open · Delete remove        │
│ Esc Skill Sources                                           │
╰─────────────────────────────────────────────────────────────╯
```

Remote detail must distinguish preserving, rotating, and removing tokens. A
blank token field never removes an existing token; `Remove token` is an explicit
destructive action.

### 5.7 Add remote skill server flow

```
╭─ Add Skill Server ──────────────────────────────────────────╮
│                                                             │
│  Server URL                                                 │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ https://skills.acme.io                                │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  Netclaw will probe:                                       │
│  /.well-known/agent-skills/index.json                      │
│                                                             │
│  [ Continue ]    [ Cancel ]                                 │
│                                                             │
│ Enter continue · Esc cancel                                 │
╰─────────────────────────────────────────────────────────────╯
```

```
╭─ Skill Server Authentication ───────────────────────────────╮
│                                                             │
│  How should Netclaw authenticate to this server?            │
│                                                             │
│  ▸ No auth required                                         │
│    Bearer token                                             │
│                                                             │
│ ↑/↓ navigate · Enter continue · Esc back                    │
╰─────────────────────────────────────────────────────────────╯
```

```
╭─ Test Skill Server ─────────────────────────────────────────╮
│                                                             │
│  ⠋ Discovering skills at https://skills.acme.io ...         │
│                                                             │
│  This may take a few seconds.                               │
│                                                             │
╰─────────────────────────────────────────────────────────────╯
```

```
╭─ Review Skill Server ───────────────────────────────────────╮
│                                                             │
│  ✓ Connected                                                │
│  ✓ 18 skills discovered                                     │
│                                                             │
│  Source name                                                │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ company-feed                                          │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  [ Add source ]    [ Back ]    [ Cancel ]                   │
│                                                             │
│ Enter apply/autosave · Esc cancel                           │
╰─────────────────────────────────────────────────────────────╯
```

If the probe fails, show `Retry`, `Edit URL`, `Edit token`, and `Save anyway`.
`Save anyway` is allowed only for reachability/auth probe failures, not for
structurally invalid URLs.

### 5.8 Remove source confirm

```
╭─ Remove Skill Source? ──────────────────────────────────────╮
│                                                             │
│  Remove source `company-feed` from Netclaw config?          │
│                                                             │
│  This does not delete remote skills or local files.         │
│  Netclaw will stop loading skills from this source.         │
│                                                             │
│  ▸ Cancel                                                   │
│    Remove source                                            │
│                                                             │
│ ↑/↓ navigate · Enter select · Esc cancel                    │
╰─────────────────────────────────────────────────────────────╯
```

### 5.9 Persistence and validation rules

- Local folders persist to `ExternalSkills.Sources`.
- Remote skill servers persist to `SkillFeeds.Feeds`.
- Completed toggles autosave immediately after validation.
- Typed drafts persist only when `Apply` / `Add source` succeeds.
- `Done` never writes.
- `Esc` cancels incomplete drafts and navigates back without writing.
- Failed validation leaves persisted files unchanged.
- Source writes preserve unrelated sources and unrelated config sections.
- Secret fields preserve existing tokens when left blank; token deletion is
  explicit.

---

## Config.6 — Search

### 6.1 Main editor

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

### 6.2 Remove credential confirm (T5)

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

## Config.9.5 — Exposure Mode

### 9.5.1 Mode selection

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
│    Tailscale Serve                                          │
│    Tailscale-served local access.                           │
│                                                             │
│    Tailscale Funnel                                         │
│    Public Tailscale funnel exposure.                        │
│                                                             │
│    Cloudflare Tunnel                                        │
│    Cloudflare-managed tunnel access.                        │
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

**Conditionality:** `Configure mode →` is enabled only when the selected mode
requires sub-config. Local has no sub-config.

**Inactive values:** Mode-specific values are preserved for later reactivation,
but only active-mode fields remain in `netclaw.json`. For example, switching
from Reverse Proxy to Local removes runtime-active `Daemon.Host` and
`Daemon.TrustedProxies` so local startup validation remains loopback-only; the
config editor keeps the dormant reverse-proxy values in editor state and restores
them if Reverse Proxy is selected again.

### 9.5.2 Reverse Proxy sub-form (T1-shaped)

```
╭─ Exposure Mode › Reverse Proxy ─────────────────────────────╮
│                                                             │
│  Trusted proxies (CIDR list):    2 configured  →            │
│                                                             │
│  [ Apply ]    [ Cancel ]                                    │
│                                                             │
│ Tab next · Enter activate · Esc cancel                      │
╰─────────────────────────────────────────────────────────────╯
```

Trusted proxies row → 9.5.6 list editor.

### 9.5.3 Tailscale Serve sub-form

```
╭─ Exposure Mode › Tailscale Serve ───────────────────────────╮
│                                                             │
│  No Netclaw-managed credentials are stored here.            │
│                                                             │
│  Tunnel process:  ▸ Managed on this host                    │
│                    Managed externally / sidecar             │
│                                                             │
│  [ Apply ]    [ Cancel ]                                    │
│                                                             │
╰─────────────────────────────────────────────────────────────╯
```

### 9.5.4 Tailscale Funnel sub-form

Same shape as Tailscale Serve, but with stronger public-exposure warning copy.

### 9.5.5 Cloudflare Tunnel sub-form

```
╭─ Exposure Mode › Cloudflare Tunnel ─────────────────────────╮
│                                                             │
│  No Netclaw-managed tunnel token is stored here.            │
│  Configure `cloudflared` outside Netclaw, then return for   │
│  validation.                                                │
│                                                             │
│  Tunnel process:  ▸ Managed on this host                    │
│                    Managed externally / sidecar             │
│                                                             │
│  [ Apply ]    [ Cancel ]                                    │
│                                                             │
╰─────────────────────────────────────────────────────────────╯
```

### 9.5.6 Trusted proxies list (T2 with `IdentifierItemEditor`)

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

## Config.9 — Security & Access

### 9.1 Security & Access page

```
╭─ Security & Access ─────────────────────────────────────────╮
│                                                             │
│  ▸ Security Posture         Team                            │
│    Enabled Features         4/6 enabled                     │
│    Audience Profiles        Customized                      │
│    Exposure Mode            Cloudflare Tunnel               │
│                                                             │
│  [ Open / Edit inline ]    [ Back ]                         │
│                                                             │
│ ↑/↓ navigate · Enter open/edit · Esc back                   │
╰─────────────────────────────────────────────────────────────╯
```

## Config.9.1 — Security Posture

### 9.1.1 Posture selection (inline T1-shaped)

Security Posture is edited inline within Security & Access. Saving `Team` or
`Public` immediately continues into the inline Enabled Features editor so the
operator can review deployment-wide runtime gates.

```
╭─ Security & Access ─────────────────────────────────────────╮
│                                                             │
│  Security Posture                                           │
│  Current posture: Personal                                  │
│                                                             │
│  ▶ [✓] Personal   Just me. Local-only by default. Tools     │
│                   have wide access.                         │
│    [ ] Team       Small team via Slack/Discord. Audience-   │
│                   restricted tools.                         │
│    [ ] Public     Open to untrusted users. Strict defaults  │
│                   and access controls.                      │
│                                                             │
│ ↑/↓ navigate · Enter save · Esc back                        │
╰─────────────────────────────────────────────────────────────╯
```

### 9.1.2 Cascade warning (T5 variant — three options)

Shown only when changing posture AND `Tools.AudienceProfiles` has been
customized away from the prior posture's defaults.

```
╭─ Posture change affects Audience Profiles ──────────────────╮
│                                                             │
│  You have customized Audience Profiles. Changing posture    │
│  will overwrite them with the new posture's defaults.       │
│                                                             │
│  ▶ Cancel - keep current posture                            │
│    Apply new posture, overwrite profiles                    │
│    Apply new posture, keep custom profiles                  │
│                                                             │
│ Default: Cancel (Esc or Enter)                              │
╰─────────────────────────────────────────────────────────────╯
```

**Doctor checks:** `ConfigSchemaDoctorCheck`, `SecurityPolicyDoctorCheck`.

---

## Config.9.3 — Enabled Features inline editor

Enabled Features is edited inline within Security & Access rather than as a
separate route. It remains deployment-wide runtime enablement; audience
exposure is configured in Audience Profiles and MCP permissions.

```
╭─ Security & Access ─────────────────────────────────────────╮
│                                                             │
│  Enabled Features                                           │
│  Toggle global runtime features. Audience exposure is       │
│  configured separately.                                     │
│                                                             │
│  ▶ [✓] memory                                               │
│    [✓] search                                               │
│    [✓] skills                                               │
│    [✓] scheduling                                           │
│    [✓] sub-agents                                           │
│    [✓] webhooks                                             │
│                                                             │
│ ↑/↓ navigate · Space/Enter toggle + save · Esc back         │
╰─────────────────────────────────────────────────────────────╯
```

---

## Config.9.4 — Audience Profiles *(addresses #1150)*

### 9.4.1 Audience selection

```
╭─ Audience Profiles ─────────────────────────────────────────╮
│                                                             │
│  System default posture: Team                               │
│  Customize audience/channel access when it should differ.   │
│  * global default audience   Customized = custom overrides  │
│                                                             │
│  ▶   Personal        Operator/local sessions                │
│    * Team            Trusted internal channels              │
│      Public          Untrusted external users               │
│                                                             │
│ ↑/↓ navigate · Enter edit audience · Esc cancel             │
╰─────────────────────────────────────────────────────────────╯
```

When a profile differs from the current system posture baseline, only that row
gets a `Customized` override marker:

```
│  ▶ Personal           Operator/local sessions               │
│  * Team               Trusted internal channels  Customized │
│    Public             Untrusted external users              │
```

### 9.4.2 Per-audience editor

```
╭─ Audience Profile: Team ────────────────────────────────────╮
│                                                             │
│  System default posture: Team                               │
│  Profile: No custom overrides                               │
│                                                             │
│  Tools                                                      │
│  ▶ [✓] File tools                                           │
│    [✓] Web                                                  │
│    [✓] Skills                                               │
│    [✓] Scheduling                                           │
│    [✓] Change workspace                                     │
│                                                             │
│  Access                                                     │
│    File scope        [◀ Session only      ▶]                │
│    Attachments       [◀ Common work files ▶]                │
│    MCP grants        [Open] netclaw mcp permissions         │
│                                                             │
│  Actions                                                    │
│    Reset overrides  [Reset]                                │
│                                                             │
│  Common work files: images, PDFs, documents, archives,      │
│  and media; excludes unknown file types.                    │
│                                                             │
│ ↑/↓ navigate · ←/→ change · Space/Enter toggle/apply        │
╰─────────────────────────────────────────────────────────────╯
```

**Key bindings critical to #1150:**

- `↑` / `↓` MUST move focus between toggle rows.
- `Space` MUST toggle the focused checkbox.
- `Enter` on a checkbox row also toggles (alternative to Space).
- `←` / `→` on a cycle row moves backward or forward through curated values.
- `Enter` on a cycle row advances to the next curated value.
- `Enter` on `MCP grants` opens the MCP permissions TUI with this audience selected.
- `Esc` from the MCP permissions root returns through Termina history to the launching page.
- `Reset overrides` replaces the full underlying audience profile, including
  hidden MCP and approval settings, with the current posture baseline mapping.

The `config-audience.tape` smoke tape explicitly exercises `↓`, `Space`,
and `Esc` to lock in the keystroke contract. Regression in arrow nav,
toggle, or return behavior is caught.

**Doctor checks:** `ConfigSchemaDoctorCheck`, `ToolAudienceProfilesDoctorCheck`.

---

## Config.8 — Telemetry & Alerting

### 8.1 Telemetry & Alerting inline editor

```
╭─ Telemetry & Alerting ──────────────────────────────────────╮
│                                                             │
│  Configure OpenTelemetry export and operational outbound    │
│  webhooks. Delivery-policy tuning is intentionally parked.  │
│                                                             │
│  Current: telemetry=disabled, outbound webhooks=1          │
│                                                             │
│  ▸ Telemetry enabled          [ ]                          │
│    OTLP endpoint              http://127.0.0.1:4317        │
│    Outbound webhook URL       https://hooks.example.com    │
│    Outbound auth header       (stored header preserved)    │
│                                                             │
│ ↑/↓ navigate · Space toggle/save · Type/Paste edit         │
│ Backspace delete · Enter apply · Esc Settings Areas        │
╰─────────────────────────────────────────────────────────────╯
```

Space or Enter on the telemetry row toggles and autosaves. `Enter` on text
rows validates and autosaves the draft. Blank auth header preserves an existing
stored header.

---

## Config.8.3 — Outbound Webhooks

### 8.3.1 List page (T3)

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

### 8.3.2 Add/edit form (T4)

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

### 8.3.3 Delete confirm (T5)

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

## Config.4 — Inbound Webhooks

```
╭─ Inbound Webhooks ──────────────────────────────────────────╮
│                                                             │
│  Global webhook enablement lives here. Route files stay     │
│  owned by `netclaw webhooks`.                               │
│                                                             │
│  ▸ Enabled                 [ ]                              │
│    Execution timeout       30 seconds                       │
│    Route authoring         netclaw webhooks                 │
│                                                             │
│  Routes: total=0, enabled=0, disabled=0, invalid=0          │
│                                                             │
│ ↑/↓ navigate · Space toggle/save · Type edit timeout        │
│ Enter apply · Esc Settings Areas                            │
╰─────────────────────────────────────────────────────────────╯
```

**Note:** route file editing remains file-based; this editor only
toggles the feature and sets the timeout. If user enables this flag
but no routes exist, `InboundWebhookRoutesDoctorCheck` (existing)
surfaces the empty-routes condition — per CLAUDE.md "fail loudly,"
we do NOT silently default to dummy routes. Failed validation rolls the
enabled toggle back and leaves files unchanged.

**Doctor checks:** `ConfigSchemaDoctorCheck`, `InboundWebhookRoutesDoctorCheck`.

---

## Skill Sources Design Note

The richer Skill Sources manager in Config.5 replaces the old compact inline
editor. Keep the source-manager treatment aligned with the T2/T3/T4 autosave
templates above: no outer `[ Save ] [ Cancel ]` row, `Apply` for typed drafts,
and explicit `Back`/`Done` rows when useful.

---

## Config.7 — Browser Automation

### 12.1 Canonical browser MCP profile editor

```
╭─ Browser Automation ────────────────────────────────────────╮
│                                                             │
│  Adds or removes Netclaw's canonical browser MCP profile.   │
│  Tool grants stay in MCP permissions.                       │
│                                                             │
│  ▸ Enabled                 [ ]                              │
│    Backend                 Playwright                       │
│    MCP permissions         open grant editor                │
│                                                             │
│  Runtime check: Playwright not installed                    │
│  Manual install guidance:                                   │
│  - dotnet tool install --global Microsoft.Playwright.CLI    │
│  - playwright install chromium                              │
│                                                             │
│ ↑/↓ navigate · Space/Enter activate · ←/→ backend/save      │
│ Esc Settings Areas                                          │
╰─────────────────────────────────────────────────────────────╯
```

Space or Enter on `Enabled` creates/removes canonical browser MCP profiles and
autosaves. `←` / `→` on Backend changes the backend preference and autosaves.
Enabling fails loudly and rolls back when runtime prerequisites are missing.
The editor prints manual install guidance; it does not run global tool installs.

**Doctor checks:** `ConfigSchemaDoctorCheck`, `BrowserAutomationDoctorCheck`.

## Daemon-restart nudge at exit

Printed to stderr after Termina teardown when (a) at least one completed action
persisted config during the session AND (b) the daemon is currently running.

```
Config saved. Restart the daemon to apply changes:
  netclaw daemon stop && netclaw daemon start
```

When the daemon is not running OR no config writes occurred, the nudge is
omitted.

**Daemon detection:** `netclaw config` uses the same lightweight probe
as `netclaw daemon status` (PID file lookup at the documented path,
falling back to a port-open check on the configured daemon port). The
probe is bounded to 250 ms; if the probe times out, the nudge is
omitted (conservative — better to miss the nudge than to falsely
suggest a restart).
