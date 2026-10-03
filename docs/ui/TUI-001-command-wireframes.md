# TUI-001: Command Wireframes

Source PRDs: `PRD-004`, `PRD-009`

## Overview

Netclaw's CLI uses **simple arg routing** in `Program.cs` for mode selection and
**Termina 0.5.1** for interactive TUI commands. Several commands use Termina TUI
rendering — all other commands use plain console output.

**Dual-mode pattern:** Some commands are **dual-mode** — bare invocation (no args)
launches Termina TUI for interactive discovery; with explicit args, they run as
single-shot CLI commands suitable for scripting.

| Command              | Interface    | Framework |
|----------------------|--------------|-----------|
| `netclaw init`       | TUI          | Termina (lightweight mode — no Akka) |
| `netclaw chat`       | TUI          | Termina (daemon mode — full stack)   |
| `netclaw provider`   | Dual-mode    | Termina (bare) / Plain CLI (with subcommand) |
| `netclaw model`      | Dual-mode    | Termina (bare) / Plain CLI (with args) |
| All others           | Plain CLI    | Plain console output                 |

## Termina Component Vocabulary

All wireframes reference actual Termina 0.5.1 components:

- **PanelNode** — bordered container with optional title
- **TextInputNode** — single or multi-line text input
- **SelectionListNode** — keyboard-navigable option list
- **TextNode** — static or dynamic text block
- **StreamingTextNode** — scrollable text that appends content in real-time
- **SpinnerNode** — animated progress indicator

---

## `netclaw init` — Onboarding Wizard (TUI)

Interactive 6-step setup wizard. Termina hosts the full wizard as a single
application with step navigation.

### Wireframe

```
╭─ Netclaw Setup ──────────────────────────────────────────────╮
│                                                              │
│  Step 2 of 6: Slack Configuration        [■■□□□□□] 33%      │
│                                                              │
│  ╭─ Slack Bot Token ───────────────────────────────────────╮ │
│  │ xoxb-************************************               │ │
│  ╰─────────────────────────────────────────────────────────╯ │
│                                                              │
│  ╭─ Slack App Token ───────────────────────────────────────╮ │
│  │ xapp-************************************               │ │
│  ╰─────────────────────────────────────────────────────────╯ │
│                                                              │
│  ℹ  Socket Mode requires both tokens. See:                  │
│     https://api.slack.com/apis/socket-mode                   │
│                                                              │
│  [Enter] Next   [Esc] Back   [Ctrl+Q] Quit                  │
╰──────────────────────────────────────────────────────────────╯
```

### Components Per Step

| Step | Title                  | Components                                            |
|------|------------------------|-------------------------------------------------------|
| 1    | LLM Provider           | SelectionListNode (OpenRouter/Anthropic/OpenAI/Ollama) + auth branch (API key or OAuth device flow) |
| 2    | Slack Configuration    | TextInputNode (bot token) + TextInputNode (app token) |
| 3    | ACL Bootstrap          | TextInputNode (owner identity) + SelectionListNode (initial channels) |
| 4    | MCP Servers            | SelectionListNode (Memorizer recommended / custom / skip) |
| 5    | Exposure Mode          | SelectionListNode (local-only default / tailscale / cloudflare) |
| 6    | Health Check           | TextNode (validation results with SpinnerNodes → checkmarks) |

### Layout Structure

```
PanelNode (outer: "Netclaw Setup")
├── TextNode (step indicator + progress bar)
├── [step-specific components]
│   ├── TextInputNode (for text/secret input, masked for tokens)
│   ├── SelectionListNode (for choice input)
│   └── SpinnerNode (for live validation)
├── TextNode (help text / contextual guidance)
└── TextNode (key bindings: Enter/Esc/Ctrl+Q)
```

### Step Detail: Health Check (Step 6)

```
╭─ Netclaw Setup ──────────────────────────────────────────────╮
│                                                              │
│  Step 6 of 6: Health Check               [■■■■■■■] 100%     │
│                                                              │
│  Verifying configuration...                                  │
│                                                              │
│  ✓  LLM provider reachable (OpenRouter)                      │
│  ✓  Slack bot token valid                                    │
│  ✓  Slack app token valid                                    │
│  ✓  MCP: memorizer connected (12 tools)                      │
│  ●  Exposure: local-only (loopback-only daemon access)       │
│                                                              │
│  All checks passed. Run `netclaw run` to start.              │
│                                                              │
│  [Enter] Finish   [Esc] Back   [Ctrl+Q] Quit                │
╰──────────────────────────────────────────────────────────────╯
```

### Behaviors

- Progress bar uses block characters (■□) rendered via TextNode
- Secret inputs (API keys, tokens) use masked TextInputNode
- Step 6 (Health Check) runs all probes in sequence with SpinnerNode → result
- [Esc] navigates back to previous step; [Ctrl+Q] exits with confirmation
- Config file written to `~/.netclaw/config/netclaw.json` on completion

---

## `netclaw chat` — Inline Developer Chat

Chat is a thin SignalR client. The daemon owns the session, tools, and
persistence. Chat uses Termina `Inline` presentation in the primary buffer.

The settled Transcript becomes normal terminal scrollback. Termina owns only
the current live region.

### Region Grammar

| Region | Purpose | Lifetime |
|--------|---------|----------|
| Session Header | Shows session, model, context, and connection state | Printed when context changes |
| Transcript | Holds immutable settled Turns | Terminal scrollback |
| Turn | Groups one prompt with its settled events and reply | Immutable after settlement |
| Live Deck | Shows current work above the bottom dock | Mutable live region |
| Activity Group | Groups parallel tools and sub-agents for one turn | Live until every row settles |
| Event Row | Shows one event identity, phase, summary, and detail state | Live or settled |
| Decision Gate | Replaces the Composer for a pending approval | Live until a decision |
| Composer | Accepts the next prompt | Live except during a decision |
| Hint Line | Shows only valid actions for the current mode | Live |
| Inspector | Shows complete safe detail for one event or Turn | Temporary full-screen view |

The Transcript has no outer border. Settled rows use indentation, symbols,
space, and color for hierarchy. The Composer can use one small border.

### Idle State at 120 Columns

```
netclaw  session tui/a1b2c3  model gpt-5.6  context 38%  daemon connected

YOU  Check the CI status and inspect any failed run.

NETCLAW
Run 2481 passed. Run 2480 failed in the Linux test job.

  ✓ tool  gh run list                         2.1s   #call-a
  ✗ tool  gh run view 2480 --log-failed       1.4s   #call-b   detail available

The failure is a deterministic path assertion. I can prepare a fix.

┌ prompt ───────────────────────────────────────────────────────────────────────────────────────────┐
│ Ask Netclaw…                                                                                     │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
Enter send  Shift+Enter newline  ↑↓ history  Esc x2 clear  Ctrl+Q quit
```

### Active Turn at 80 Columns

```
netclaw  tui/a1b2c3  gpt-5.6  context 38%

YOU  Check the CI status and inspect any failed run.

THOUGHT  ● analyzing repository and workflow state                         3s

ACTIVITY  2 active · 1 complete
  ✓ tool   gh run list                                      2.1s  #call-a
  ● tool   gh run view 2480                                 1.4s  #call-b
    └─ ● agent  test-diagnostics                         2 tools  #run-7
  ● tool   read failure log                                  0.8s  #call-c

Working…  Ctrl+C interrupt  Ctrl+O detail  Ctrl+Q quit

QUEUED  2 messages
  1  Also inspect the failed test history.
  2  Then propose the smallest deterministic fix.

MESSAGE
  Ask Netclaw…
```

The Live Deck shows current work above the bottom dock. The Composer remains
available during an active turn. The Queue Shelf shows every accepted prompt.
The session actor includes the complete FIFO set in one follow-up model call.

### Decision Gate at 80 Columns

```
APPROVAL  shell wants permission

Target   dotnet test
Effect   starts a local process
Scope    this exact command in /work/netclaw

  Allow once     Always allow     Deny

Enter decide  Esc deny  Ctrl+O full detail  ←→ choice  Ctrl+Q quit
```

The expanded state keeps the selected decision. Page Up and Page Down move a
bounded detail viewport. Approval content displays control bytes as safe text.

### Narrow State at 40 Columns

```
netclaw  tui/a1b2c3  38%

YOU  Check CI and inspect failures.

ACTIVITY  2 active
  ✓ gh run list             #call-a
  ● gh run view             #call-b
    └─ ● test-diagnostics    #run-7

Working…  ^C stop  ^O detail  ^Q quit
```

At 40 columns, optional duration, model, and count detail leaves first. Event
identity, lifecycle, error state, input text, and detail availability remain.

### Responsive Rules

| Width | Session Header | Event Row | Hint Line |
|-------|----------------|-----------|-----------|
| 120+ | session, model, context, daemon, usage | phase, kind, full summary, duration, short ID | complete action labels |
| 80-119 | session, model, context | phase, kind, summary, duration, short ID | common action labels |
| 60-79 | session, context | phase, short kind, clipped summary, short ID | compact action labels |
| 40-59 | session suffix, context | phase, clipped name, short ID | control-key labels |

No responsive rule merges unrelated events onto one line. Long content remains
available through the Inspector and semantic copy.

### Event Forms

- User and assistant text use quiet labels and no side rail.
- Thought uses one active row and a settled duration or token summary.
- Tool rows use `CallId` as their stable key.
- Sub-agent rows use `RunId` and show their parent `CallId` relation.
- File rows show path, change kind, and available metadata.
- Error rows show category, message, and short correlation ID.
- Usage rows retain input, output, cached, and reasoning token classes.
- Compaction rows retain cleared-result and summary counts.
- Unknown output creates a visible diagnostic row.

### Flow Control

```
Composer --Enter--> Live Deck --settled events--> Transcript
    |                    |
    |                    +--approval--> Decision Gate --decision--> Live Deck
    |                    +--inspect--> Inspector --close--> queued output commit
    +--Enter while live--> Queue Shelf --turn end--> one FIFO follow-up call
```

Settled events print once in chronological order. A settled event never returns
to the Live Deck. Parallel completion updates only the matching stable identity.

### Input and Copy

- Bare `Enter` submits the prompt.
- `Shift+Enter` adds one newline.
- Up and Down traverse history at text boundaries.
- Down past the newest prompt restores the saved draft.
- Two Escape keys inside the defined virtual-time window clear prompt text.
- One Escape keeps prompt text.
- A pending approval owns Escape and paste before the Composer.
- `Ctrl+O` changes compact and expanded detail.
- Semantic copy can copy one complete event or one complete Turn.
- Semantic copy excludes ANSI bytes, borders, rails, spinners, and hints.
- A copy failure keeps the selected data and shows a visible error.

Terminal-native selection cannot exclude selected glyphs. The borderless
Transcript prevents border and corner glyphs from entering ordinary selection.

---

## `netclaw provider` — Dual-Mode Provider Management

**Bare invocation** launches Termina TUI — a guided walk-through that reuses the
wizard's Step 1 components (provider selection, auth method branching, OAuth
device flow, credential entry, model selection). This is the "hold my hand" path.

**With subcommands** (`add`, `list`, `remove`) runs as single-shot plain CLI.

### Wireframe (TUI mode — bare `netclaw provider`)

```
╭─ Provider Setup ─────────────────────────────────────────────╮
│                                                              │
│  Configure a new LLM provider                               │
│                                                              │
│  Select provider type:                                       │
│                                                              │
│  ╭───────────────────────────────────────────────────────╮   │
│  │  ● Anthropic          (OAuth + API key)               │   │
│  │    OpenAI             (OAuth + API key)               │   │
│  │    OpenRouter         (API key)                       │   │
│  │    Ollama             (local, no auth)                │   │
│  ╰───────────────────────────────────────────────────────╯   │
│                                                              │
│  [Enter] Select   [Esc] Cancel   [Ctrl+Q] Quit              │
╰──────────────────────────────────────────────────────────────╯
```

### Behaviors

- Reuses the same provider setup components as `netclaw init` Step 1
- After provider is configured, prompts for model role assignment
- New provider is added to `~/.netclaw/config/netclaw.json`
- Secrets written to `~/.netclaw/config/secrets.json`
- OAuth device flow uses same Termina `SpinnerNode` poll-wait as wizard

### Single-Shot Examples

```
$ netclaw provider add --name my-anthropic --type anthropic --auth-method api-key
API key: ****
Provider 'my-anthropic' configured.

$ netclaw provider list
Name            Type         Auth         Status
my-anthropic    anthropic    API key      ✓ valid
local-ollama    ollama       none         ✓ reachable
my-openrouter   openrouter   API key      ✓ valid

$ netclaw provider remove my-openrouter
⚠ Model 'Compaction' references this provider. Reassign first.
```

---

## `netclaw model` — Dual-Mode Model Selection

**Bare invocation** launches Termina TUI — a tree-based model browser showing all
configured providers, their available models (via live discovery or curated defaults), and
current role assignments. Operator selects a role, browses models, and assigns.

**With args** (`--role`, `--provider`, `--model`) runs as single-shot assignment.

### Wireframe (TUI mode — bare `netclaw model`)

```
╭─ Model Selection ────────────────────────────────────────────╮
│                                                              │
│  Current assignments:                                        │
│    Main:       claude-sonnet-4-20250514 (my-anthropic)       │
│    Fallback:   qwen3:30b (local-ollama)                      │
│    Compaction: qwen3:8b (local-ollama)                       │
│                                                              │
│  Select role to change: [Main ▾]                             │
│                                                              │
│  Available models:                                           │
│  ├── my-anthropic (OAuth ✓)                                  │
│  │   ├── claude-sonnet-4-20250514 (128k) ← current          │
│  │   ├── claude-haiku-4-5-20251001 (200k)                    │
│  │   └── claude-opus-4-20250514 (200k)                       │
│  ├── local-ollama                                            │
│  │   ├── qwen3:30b (32k)                                    │
│  │   └── qwen3:8b (32k)                                     │
│  └── my-openrouter (API key ✓)                               │
│      ├── google/gemini-2.5-pro                               │
│      └── anthropic/claude-sonnet-4-20250514                  │
│                                                              │
│  [Enter] Select   [Esc] Cancel   [Ctrl+Q] Quit              │
╰──────────────────────────────────────────────────────────────╯
```

### Layout Structure

```
PanelNode (outer: "Model Selection")
├── TextNode (current role assignments)
├── SelectionListNode (role selector: Main/Fallback/Compaction)
├── [tree view of providers and models]
│   ├── TextNode (provider name + auth status)
│   └── SelectionListNode (models under that provider)
└── TextNode (key bindings)
```

### Behaviors

- Tree populated via model discovery (live → curated defaults) across all
  configured providers
- SpinnerNode shown while discovering models from each provider
- Provider auth status shown inline (✓ valid / ⚠ expired / ✗ unreachable)
- Current model for selected role marked with `← current`
- On selection: updates `Models` section of `netclaw.json`, confirms change
- Model selector component is shared with `netclaw init` Step 1c

### Single-Shot Examples

```
$ netclaw model --role main --provider my-anthropic --model claude-sonnet-4-20250514
Main model set to claude-sonnet-4-20250514 (my-anthropic).

$ netclaw model --role fallback --provider local-ollama --model qwen3:30b
Fallback model set to qwen3:30b (local-ollama).
```

---

## `netclaw doctor` — Plain CLI Output (No TUI)

Simple check-and-report command. Runs startup checks, prints color-coded
results, exits with appropriate exit code. Not interactive — no Termina TUI.

### Output Example

```
$ netclaw doctor

Netclaw Doctor

Checking startup requirements...

  ✓  Config file valid
  ✓  ACL valid (3 channel rules, 2 tool grants)
  ✓  LLM provider reachable (OpenRouter)
  ✗  Slack bot token invalid or expired
     Fix: netclaw init --step slack
  ✓  MCP: memorizer connected (12 tools)
  ⚠  MCP: searxng needs auth
     Fix: Add API key to ~/.netclaw/config/netclaw.json

Result: 1 error, 1 warning. Netclaw cannot start.
```

### Behavior

- Runs all startup checks in sequence
- Color-coded output: ✓ green, ⚠ yellow, ✗ red
- Errors include remediation commands (e.g., `netclaw init --step slack`)
- Exit code: 0 (all pass), 1 (errors), 2 (warnings only)
- No interactivity — suitable for scripting and CI

---

## Commands That Stay Plain CLI (No TUI)

All of the following commands use standard console output.
No Termina TUI components are used.

| Command                              | Output Style        |
|--------------------------------------|---------------------|
| `netclaw doctor`                     | Check list          |
| `netclaw config show`                | Formatted text/JSON |
| `netclaw config validate`            | Validation results  |
| `netclaw provider add\|list\|remove`   | Simple CRUD         |
| `netclaw model --role ... --model ...` | Single-shot assign |
| `netclaw acl validate\|test\|explain`  | Policy check results|
| `netclaw session list\|inspect`        | Tabular             |
| `netclaw project list\|add\|remove`    | Simple CRUD         |
| `netclaw environment scan\|show`       | Scan results        |
| `netclaw memory show`                | Display memory files|
| `netclaw schedule list\|show\|pause\|resume\|delete` | Tabular |
| `netclaw tools list\|policy`           | Tabular             |
| `netclaw mcp list\|validate\|test`     | Validation results  |
| `netclaw test smoke`                 | Test results        |
| `netclaw personality reset`          | Confirmation        |
| `netclaw run`                        | Daemon (no TUI)     |

Note: `netclaw provider` (bare) and `netclaw model` (bare) are dual-mode — they
launch Termina TUI when invoked without subcommands or args. See wireframes above.

### `netclaw run` — Daemon Mode

Starts the full Netclaw process: Slack Socket Mode adapter, Akka actor system,
scheduled task timers, health endpoints. No TUI — logs to console/file.
This is the primary production entry point.

---

## Cross-References

- CLI command surface: PRD-004
- TUI adapter contract: PRD-009
- Ops console (web): UI-001
- Daemon architecture and mode selection: SPEC-011
- Termina 0.5.1: implementation decision
