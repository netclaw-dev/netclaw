# TUI-003: Simplified `netclaw init` Wireframes

Source PRDs: `PRD-004`, `PRD-001`

Backing OpenSpec change: `openspec/changes/simplify-netclaw-init/`

Companion: `TUI-001-command-wireframes.md` (prior 6-step init wizard,
superseded by this document), `TUI-002-netclaw-config-wireframes.md`
(the `netclaw config` command that owns post-bootstrap edits).

## Overview

`netclaw init` is trimmed from 12 steps to three: LLM provider,
identity, security posture. The goal is time-to-first-chat. Everything
else (channels, search, webhooks, exposure mode, audience profiles,
skill feeds, external skill directories, browser automation, MCP
servers) moves to `netclaw config` (see TUI-002).

Existing-config detection is now explicit: re-running over an existing
install refuses with helpful pointers, or accepts `--force` to back
up and reset.

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
  ├── Init.2  Identity (agent name, user name, timezone)
  ├── Init.3  Security Posture
  └── Init.4  Post-flight (health-check, summary) ─── exit + stderr nudge

netclaw init  (existing config detected, no --force)
  └── Init.E1  Refuse + suggest `netclaw config` or `netclaw init --force`

netclaw init --force  (existing config detected)
  └── Init.E2  Backup confirm ──→ Init.1 (proceeds as fresh)

netclaw init --force  (no existing config)
  └── Init.1 (proceeds as fresh; no backup screen)
```

---

## Init.1 — Provider selection

Reuses existing `ProviderStepViewModel` (refactored to `ISectionEditor`
in `section-editor-abstraction` change). After the provider type is
picked, the existing auth sub-flow runs (auth method → endpoint → API
key or OAuth device flow → model selection). Behavior unchanged from
prior versions.

```
╭─ Netclaw Setup — Step 1 of 3: LLM Provider ─────────────────╮
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

**Reentrancy:** in the rare case `netclaw init` runs over existing
config (only via `--force` reset; otherwise the command refuses
at Init.E1), the provider selector pre-fills the existing provider
type. API key field renders empty per the secret-handling contract
(`configured — leave blank to keep`).

---

## Init.2 — Identity

Trimmed `IdentityStepViewModel` (see Change C tasks 5.x). Drops the
prior webhook URL prompt, the workspaces-directory prompt, and the
communication-style prompt. Keeps agent name, user name, timezone.

```
╭─ Netclaw Setup — Step 2 of 3: Identity ─────────────────────╮
│                                                             │
│  Your provider is configured. Now let's set up the agent.   │
│                                                             │
│  Agent name:                                                │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ Netclaw                                                │ │
│  ╰────────────────────────────────────────────────────────╯ │
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
│  [ Next ]    [ Back ]    [ Cancel ]                         │
│                                                             │
│ Tab next · Enter activate · Esc cancel                      │
╰─────────────────────────────────────────────────────────────╯
```

**Transitions:**

- `Next` → Init.3.
- `Back` → Init.1.
- `Cancel` → discard confirm → exit.

**Validation:** Agent name required, no whitespace. User name required.
Timezone validates against `TimeZoneInfo.FindSystemTimeZoneById`.

**Dropped fields' defaults:** webhook URL is left unset (operators add
operational webhooks via `netclaw config → Outbound Webhooks`).
Workspaces directory defaults to `~/.netclaw/workspaces`. Communication
style defaults to neutral. These remain editable via file edit for now
(future Identity section editor in `netclaw config` is out of MVP
scope).

---

## Init.3 — Security Posture

Reuses existing `SecurityPostureStepViewModel`.

```
╭─ Netclaw Setup — Step 3 of 3: Security Posture ─────────────╮
│                                                             │
│  How will Netclaw be used?                                  │
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
│  [ Next ]    [ Back ]    [ Cancel ]                         │
│                                                             │
│ ↑/↓ navigate · Tab to buttons · Enter activate              │
╰─────────────────────────────────────────────────────────────╯
```

**Transitions:**

- `Next` (Enter on Next button OR Enter on a posture row) → applies
  posture-default `Tools.AudienceProfiles` mapping in-memory →
  proceeds to Init.4 (terminal write + health check).
- `Back` → Init.2.

**Posture cascade applied non-interactively (no separate feature
selection step):**

| Posture    | Audience.Personal | Audience.Team               | Audience.Public            | Shell mode    |
|------------|-------------------|-----------------------------|----------------------------|---------------|
| Personal   | all features on   | n/a (Personal-only)         | n/a                        | HostAllowed   |
| Team       | all features on   | search+memory+skills on; webhooks off | webhooks off; memory off | SandboxOnly   |
| Enterprise | search+memory on  | search+memory on            | nothing on                 | SandboxOnly   |

Operators override per-audience post-install via `netclaw config →
Audience Profiles`.

---

## Init.4 — Post-flight

After Init.3 applies posture, the wizard writes merged config + secrets
+ runs the existing health check + shows results.

```
╭─ Netclaw Setup — Setup Complete ────────────────────────────╮
│                                                             │
│  ✓ Provider configured: Anthropic (claude-sonnet-4-6)       │
│  ✓ Identity set: Netclaw (aaron, America/Los_Angeles)       │
│  ✓ Posture: Personal                                        │
│  ✓ Configuration written to ~/.netclaw/config/netclaw.json  │
│  ✓ Health check passed                                      │
│                                                             │
│  ──────                                                     │
│                                                             │
│  Run `netclaw chat` to start talking to your agent.         │
│  Run `netclaw config` to set up channels, search, webhooks, │
│  external skills, browser automation, and more.             │
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
the errors and a `[ Back to Posture ]` action instead of `[ Done ]`.
Operator returns to Init.3 to fix.

### Post-flight when `--force` was used

When `netclaw init --force` triggered a backup, the post-flight screen
appends a `.bak` file disclosure section so operators know where the
prior config went:

```
│  ──────                                                     │
│  Previous configuration backed up to:                       │
│    ~/.netclaw/config/netclaw.json.bak.1716508800            │
│    ~/.netclaw/config/secrets.json.bak.1716508800            │
│                                                             │
│  Restore manually if needed.                                │
```

The same paths are printed to stderr after Termina teardown.

---

## Init.E1 — Existing config refusal

Rendered when `netclaw init` is invoked, `~/.netclaw/config/netclaw.json`
exists, and `--force` was not passed.

```
╭─ Netclaw is already initialized ────────────────────────────╮
│                                                             │
│  Found existing configuration:                              │
│    ~/.netclaw/config/netclaw.json                           │
│                                                             │
│  To edit your configuration interactively, run:             │
│    netclaw config                                           │
│                                                             │
│  To start over from scratch (existing config backed up):    │
│    netclaw init --force                                     │
│                                                             │
│  [ OK ]                                                     │
│                                                             │
│ Enter exit                                                  │
╰─────────────────────────────────────────────────────────────╯
```

**Non-interactive variant** (when stdout is not a TTY, e.g. CI):
prints the same text to stderr and exits non-zero. The interactive
variant exits zero on acknowledgement.

---

## Init.E2 — Force-reset backup confirm

Rendered when `netclaw init --force` runs and existing config is
detected.

```
╭─ Reset Netclaw configuration? ──────────────────────────────╮
│                                                             │
│  This will:                                                 │
│    • Move netclaw.json → netclaw.json.bak.<timestamp>       │
│    • Move secrets.json → secrets.json.bak.<timestamp>       │
│    • Start setup from scratch                               │
│                                                             │
│  Your old config is preserved as a .bak file; you can       │
│  restore it manually if needed.                             │
│                                                             │
│  Type "reset" to confirm:                                   │
│  ╭────────────────────────────────────────────────────────╮ │
│  │                                                        │ │
│  ╰────────────────────────────────────────────────────────╯ │
│                                                             │
│  ▸ [ Cancel ]    [ Reset and continue ]                     │
│                                                             │
╰─────────────────────────────────────────────────────────────╯
```

**Type-to-confirm here because this is genuinely destructive** (running
config + secrets get moved aside, fresh setup writes new ones).
Single-Y/N is insufficient.

**Transitions:**

- `Cancel` → exit zero. Config unchanged.
- `Reset and continue` (enabled only when "reset" typed) → backup
  performed (rename atomically; timestamp generated once per
  invocation so both files share a suffix) → proceed to Init.1.

**Non-TTY refusal:** `netclaw init --force > /dev/null 2>&1` cannot
prompt for the type-to-confirm. The command SHALL refuse in non-TTY
contexts with `--force` requires interactive confirm and exit non-zero.

**`--force` over no existing config:** silently behaves as plain
`netclaw init` (no backup screen, no extra prompt).

**Backup timestamp collision avoidance:** the timestamp suffix uses
unix-milliseconds (`netclaw.json.bak.<millis>`). On the extremely
unlikely event of a collision (two `--force` invocations in the same
millisecond), an auto-increment suffix is appended
(`netclaw.json.bak.<millis>-1`).
