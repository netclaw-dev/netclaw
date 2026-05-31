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

Leaf editors remain reentrant by construction and validate before save, but
the root dashboard groups them by operator intent.

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
  └── Config.0  Domain dashboard
        ├── Config.1   Inference Providers  ──→ routes to `netclaw provider`
        ├── Config.2   Models               ──→ routes to `netclaw model`
        ├── Config.3   Channels
        │     ├── Slack
        │     ├── Discord
        │     └── Mattermost
        ├── Config.4   Inbound Webhooks
        ├── Config.5   Skill Sources
        │     ├── External Skill Directories
        │     └── Skill Feeds
        ├── Config.6   Search
        ├── Config.7   Browser Automation
        ├── Config.8   Telemetry & Alerting
        │     ├── Telemetry
        │     └── Outbound Webhooks
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
│                                                             │
│  ↑/↓ to navigate, Space to toggle, Enter to open selected.  │
│  Unconfigured adapters open first-time setup. Configured    │
│  adapters open management without prompting for credentials.│
│                                                             │
│ ↑/↓ navigate · Space toggle · Enter open · d save           │
╰─────────────────────────────────────────────────────────────╯
```

Unconfigured adapters reuse the original `netclaw init` sub-flow visuals:

- Slack: bot token -> Socket Mode app token -> channel names/IDs -> DMs ->
  user access choice -> allowed user IDs when restricted.
- Discord: bot token -> channel IDs -> DMs -> user access choice -> allowed
  user IDs when restricted.
- Mattermost: server URL -> bot token -> channel IDs -> DMs -> user access
  choice -> allowed user IDs when restricted -> optional callback URL.

**Save model:** First-time setup sub-flows update in-memory state, then drop
the operator directly into Channels & Permissions so every new channel gets an
explicit audience before save. Disk write happens only when the operator
returns to the picker and presses `d`/Done. The save uses the shared
config-editor merge pipeline, preserving unrelated config and secrets.

**Secret reentrancy:** Configured adapters do not ask for credentials on
normal re-entry. Secret fields are shown only from first-time setup or explicit
Rotate credentials. If a stored secret exists, the field shows
`(configured - leave blank to keep)`. Blank submission preserves the existing
secret; entering a new value replaces it.

**Disabled adapters:** Toggling off a previously configured adapter writes
`<Adapter>.Enabled = false` and preserves dormant channel/user fields plus
stored credentials. The daemon ignores those fields while the adapter is
disabled.

**Validation:** Save blocks missing required credentials for enabled adapters
and invalid Mattermost server URLs. Connection probes remain doctor-owned in
this first pass.

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
preserved. Reset is immediate: confirming reset deletes the adapter config
section and its secrets before returning to the picker/saved screen.

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
│                                                             │
│  Audience controls which tools and data this channel can use│
│                                                             │
│ ↑/↓ navigate · ←/→ audience · Enter edit · a add · d remove │
╰─────────────────────────────────────────────────────────────╯
```

Channel rows write `<Adapter>.AllowedChannelIds` and
`<Adapter>.ChannelAudiences[channelId]`. The DM row writes
`<Adapter>.AllowDirectMessages` plus `<Adapter>.ChannelAudiences["dm"]`.
Removing a channel removes both the channel ID and its audience mapping. DM
audience is preserved when DMs are disabled so re-enabling DMs restores the
operator's last chosen audience.

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
replace only that secret.

---

## Config.5 — Skill Sources

### 5.1 Skill Sources sub-page

```
╭─ Skill Sources ─────────────────────────────────────────────╮
│                                                             │
│  ▸ External Skill Directories   2 configured                │
│    Skill Feeds                  1 configured                │
│                                                             │
│  [ Open ]    [ Back ]                                       │
│                                                             │
│ ↑/↓ navigate · Enter open · Esc back                        │
╰─────────────────────────────────────────────────────────────╯
```

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

### 8.1 Telemetry & Alerting sub-page

```
╭─ Telemetry & Alerting ──────────────────────────────────────╮
│                                                             │
│  ▸ Telemetry                Disabled                        │
│    Outbound Webhooks        2 configured                    │
│                                                             │
│  [ Open ]    [ Back ]                                       │
│                                                             │
│ ↑/↓ navigate · Enter open · Esc back                        │
╰─────────────────────────────────────────────────────────────╯
```

### 8.2 Telemetry editor

```
╭─ Telemetry & Alerting › Telemetry ──────────────────────────╮
│                                                             │
│  Telemetry enabled:         [ X ] yes                       │
│                                                             │
│  OTLP endpoint:                                              │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ http://127.0.0.1:4317                                 │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  gRPC OTLP only. Netclaw expects collector port 4317.      │
│                                                             │
│  [ Save ]    [ Cancel ]                                     │
│                                                             │
│ Tab next · Enter activate · Esc cancel                      │
╰─────────────────────────────────────────────────────────────╯
```

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

## Config.5.2 — External Skill Directories

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

## Config.5.3 — Skill Feeds

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

## Config.7 — Browser Automation

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
