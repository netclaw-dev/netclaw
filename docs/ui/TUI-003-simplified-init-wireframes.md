# TUI-003: Simplified `netclaw init` Wireframes

Source PRDs: `PRD-004`, `PRD-001`

Backing OpenSpec change: `openspec/changes/simplify-netclaw-init/`

Companion: `TUI-001-command-wireframes.md` (prior 6-step init wizard,
superseded by this document), `TUI-002-netclaw-config-wireframes.md`
(the `netclaw config` command that owns post-bootstrap edits).

## Overview

`netclaw init` is trimmed to bootstrap plus a small existing-install menu.
The goal is time-to-first-chat. Everything else (channels, search,
webhooks, exposure mode, audience profiles, skill feeds, external skill
directories, browser automation, MCP servers, and other ongoing tuning)
moves to `netclaw config` (see TUI-002).

Existing-config detection is explicit: re-running over an existing install
opens a small action menu instead of replaying the full wizard.

## Termina Component Vocabulary

Same as TUI-001 / TUI-002:

- **PanelNode** — bordered container with optional title
- **TextInputNode** — single or multi-line text input (masked variant for secrets)
- **SelectionListNode** — keyboard-navigable option list
- **TextNode** — static or dynamic text block
- **SpinnerNode** — animated progress indicator (post-flight health check)

## Conventions

Glyphs and keystrokes follow TUI-002 conventions. Init-specific:

- Title bar shows step indicator `Step <n> of 3: <title>`.
- Step navigation: Tab cycles fields; Enter on Next advances; Enter or
  Esc on Back returns; Esc on a step with dirty state triggers discard
  confirm (see TUI-002 T7).

---

## Navigation tree

```
netclaw init  (fresh install — no existing config)
  ├── Init.1  Provider selection (+ existing auth sub-flow)
  ├── Init.2  Identity (workspaces directory, user name, timezone)
  ├── Init.3  Security Posture
  ├── Init.4  Enabled Features (Team/Public only)
  └── Init.5  Post-flight (health-check, summary) ─── exit + stderr nudge

netclaw init  (existing config detected)
  ├── Init.E1  Existing-install menu
  ├── Init.2   Identity re-entry form (prefilled)
  ├── Init.E2  Start-over scope chooser
  ├── Init.E3  First destructive confirmation
  ├── Init.E4  Second destructive confirmation
  └── Init.1 / Init.2 / Init.3 / Init.4 / Init.5 as applicable
```

---

## Init.1 — Provider selection

Reuses existing `ProviderStepViewModel` (refactored to `ISectionEditor`
in `section-editor-abstraction` change). After the provider type is
picked, the existing auth sub-flow runs (auth method → endpoint → API
key or OAuth device flow → model selection). Behavior unchanged from
prior versions.

```
╭─ Netclaw Setup — Step 1: LLM Provider ──────────────────────╮
│                                                             │
│  Choose your LLM provider:                                  │
│                                                             │
│  ▸ Anthropic                                                │
│    OpenAI                                                   │
│    OpenRouter                                               │
│    GitHub Copilot                                           │
│    Ollama (local, no API key)                               │
│    OpenAI-compatible (custom endpoint)                      │
│                                                             │
│ ↑/↓ navigate · Enter select · Esc quit                      │
╰─────────────────────────────────────────────────────────────╯
```

**Transitions:**

- `Enter` → existing auth sub-flow (TUI-001 covers the sub-flow shapes).
- `Esc` → quit setup (with discard confirm if anything was entered).

**Reentrancy:** when existing-install init routes into an init-owned
sub-flow, the provider selector pre-fills the existing provider type.
API key fields render empty per the secret-handling contract
(`configured — leave blank to keep`).

---

## Init.2 — Identity

Identity remains init-owned. The form reuses the familiar identity step,
prefilled from the existing install on re-entry, and hands off to the
bot-assisted identity conversation afterward.

```
╭─ Netclaw Setup — Step 2: Identity ──────────────────────────╮
│                                                             │
│  Your provider is configured. Now let's set up the agent.   │
│                                                             │
│  Your name (what the agent calls you):                      │
│  ╭────────────────────────────────────────────────────────╮ │
│  │                                                        │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  Timezone (IANA name):                                      │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ America/Los_Angeles                                    │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  Workspaces directory:                                      │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ ~/.netclaw/workspaces                                  │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  [ Next ]    [ Back ]    [ Cancel ]                         │
│                                                             │
│ Tab next · Enter activate · Esc cancel                      │
╰─────────────────────────────────────────────────────────────╯
```

**Transitions:**

- `Next` → Init.3.
- `Back` → Init.1.
- `Cancel` → discard confirm → exit.

**Validation:** User name required. Timezone validates against
`TimeZoneInfo.FindSystemTimeZoneById`. Workspaces directory must be a
valid local path.

On completion, the flow can continue into the existing bot-assisted
identity conversation that regenerates `SOUL.md` and `TOOLING.md`.

---

## Init.3 — Security Posture

Reuses existing `SecurityPostureStepViewModel`.

```
╭─ Netclaw Setup — Step 3: Security Posture ──────────────────╮
│                                                             │
│  How will Netclaw be used?                                  │
│                                                             │
│  ▸ Personal                                                 │
│    Just me. Local-only by default. Tools have wide access.  │
│                                                             │
│    Team                                                     │
│    Small team via Slack/Discord. Audience-restricted tools. │
│                                                             │
│    Public                                                   │
│    Open to untrusted users. Strict defaults and access      │
│    controls.                                                │
│                                                             │
│  [ Next ]    [ Back ]    [ Cancel ]                         │
│                                                             │
│ ↑/↓ navigate · Tab to buttons · Enter activate              │
╰─────────────────────────────────────────────────────────────╯
```

**Transitions:**

- `Next` (Enter on Next button OR Enter on a posture row) → applies
  posture-default `Tools.AudienceProfiles` mapping in-memory.
- `Personal` proceeds directly to Init.5.
- `Team` and `Public` proceed to Init.4 (Enabled Features).
- `Back` → Init.2.

**Shell mode remains global:** the posture step writes the global shell
default. It does not create per-audience shell settings.

---

## Init.4 — Enabled Features

Shown only for `Team` and `Public`. This is deployment-wide runtime
enablement, not per-audience access policy.

```
╭─ Netclaw Setup — Step 4: Enabled Features ──────────────────╮
│                                                             │
│  Choose which runtime features are enabled for this         │
│  deployment. Audience exposure is configured later in       │
│  `netclaw config`.                                          │
│                                                             │
│  [ X ] memory                                               │
│  [ X ] search                                               │
│  [ X ] skills                                               │
│  [ X ] scheduling                                           │
│  [ X ] sub-agents                                           │
│  [ X ] webhooks                                             │
│                                                             │
│  [ Next ]    [ Back ]    [ Cancel ]                         │
│                                                             │
│ ↑/↓ navigate · Space toggle · Tab to buttons                │
╰─────────────────────────────────────────────────────────────╯
```

`Personal` skips this step. `Team` and `Public` use different defaults,
but the toggles always write deployment-wide `Enabled` flags.

---

## Init.5 — Post-flight

After the final step, the wizard writes merged config + secrets, runs the
existing health check, and shows results.

```
╭─ Netclaw Setup — Setup Complete ────────────────────────────╮
│                                                             │
│  ✓ Provider configured: Anthropic (claude-sonnet-4-6)       │
│  ✓ Identity set: aaron, America/Los_Angeles                 │
│  ✓ Posture: Personal                                        │
│  ✓ Enabled Features: all defaults applied                   │
│  ✓ Configuration written to ~/.netclaw/config/netclaw.json  │
│  ✓ Health check passed                                      │
│                                                             │
│  ──────                                                     │
│                                                             │
│  Run `netclaw chat` to start talking to your agent.         │
│  Run `netclaw config` to set up providers, models,          │
│  channels, webhooks, search, security, and more.            │
│                                                             │
│  [ Done ]                                                   │
│                                                             │
│ Enter exit                                                  │
╰─────────────────────────────────────────────────────────────╯
```

**Transitions:**

- `Enter` → Termina tears down. The same two-line nudge is also printed
  to stderr after exit so users see it even after the TUI clears.

**Failure path:** if health check fails (doctor errors), the page shows
the errors and a `[ Back ]` action instead of `[ Done ]`. The operator
returns to the previous applicable step to fix.

## Init.E1 — Existing-install menu

Rendered when `netclaw init` detects an existing install.

```
╭─ Existing Netclaw install detected ─────────────────────────╮
│                                                             │
│  Choose what to do next.                                    │
│                                                             │
│  ▸ Redo identity setup                                      │
│    Open configuration editor                                │
│    Start over from scratch                                  │
│    Cancel                                                   │
│                                                             │
│ ↑/↓ navigate · Enter select · Esc cancel                    │
╰─────────────────────────────────────────────────────────────╯
```

## Init.E2 — Start-over scope chooser

Rendered after `Start over from scratch`.

```
╭─ Start over from scratch ───────────────────────────────────╮
│                                                             │
│  Choose reset scope.                                        │
│                                                             │
│  ▸ Reset setup only                                         │
│    Archive config, secrets, pairing/bootstrap state, and    │
│    identity files. Preserve DB, logs, projects, schedules,  │
│    environment, and skills.                                 │
│                                                             │
│    Full reset                                               │
│    Wipe the full Netclaw home except the binary payload.    │
│                                                             │
│    Cancel                                                   │
│                                                             │
│ ↑/↓ navigate · Enter select · Esc cancel                    │
╰─────────────────────────────────────────────────────────────╯
```

## Init.E3 / Init.E4 — Double confirmation

Both reset scopes require two explicit confirmations before mutation.
Default focus stays on the non-destructive option.
