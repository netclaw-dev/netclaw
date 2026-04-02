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

## `netclaw chat` — Interactive Agent Prompt (TUI)

Full interactive chat session with the Netclaw agent. Hosts the actor system
in-process. Session entity key: `tui/{uuid}`.

### Wireframe

```
╭─ Netclaw Chat ─────────────────── session: tui/a1b2c3 ──────╮
│                                                              │
│  System: Personality loaded. 5 tools. Memorizer connected.   │
│                                                              │
│  You: Check the CI status on netclaw and summarize           │
│                                                              │
│  ╭─ Tool Activity ──────────────────────────────────────╮    │
│  │ ✓ shell: gh run list --limit 3           (2.1s)      │    │
│  │ ● web_fetch: github.com/...actions        (...)      │    │
│  ╰──────────────────────────────────────────────────────╯    │
│                                                              │
│  Netclaw: Here's your CI status:                             │
│                                                              │
│  | Run  | Branch | Status  | Duration |                      │
│  |------|--------|---------|----------|                      │
│  | #42  | dev    | ✓ pass  | 3m 12s   |                      │
│  | #41  | dev    | ✓ pass  | 2m 58s   |                      │
│  | #40  | feat/x | ✗ fail  | 1m 04s   |                      │
│                                                              │
│  Run #40 failed on feat/x. Want me to investigate?           │
│  ●                                                           │
╰──────────────────────────────────────────────────────────────╯
╭─ Input ──────────────────────────────────────────────────────╮
│ Yes, show me the failure logs and                            │
│ see if it's a flaky test or a real issue.                    │
│ █                                                            │
╰──────────────────────────────────────────────────────────────╯
 [Enter] Send  [PgUp/PgDn] Scroll  [Ctrl+Q] Quit  ✓ MCP (2/2)
```

### Layout Structure

```
PanelNode (outer: "Netclaw Chat", subtitle: session ID)
├── StreamingTextNode (scrollable chat history, fills available space)
│   ├── System messages (personality, tool count, MCP status)
│   ├── User messages (prefixed "You:")
│   ├── Tool Activity PanelNode (inline, collapsible)
│   │   ├── TextNode (✓ completed: tool name + duration, green)
│   │   └── SpinnerNode (● in-progress: tool name, yellow)
│   └── Assistant messages (prefixed "Netclaw:", streamed via SpinnerSegment)
│
PanelNode (input: "Input")
├── TextInputNode (multi-line, 3 rows, fixed at bottom)
│
TextNode (status bar: key bindings + MCP indicator)
```

### Key Behaviors

- **StreamingTextNode** fills most of the screen, scrollable with PgUp/PgDn
- **TextInputNode** is multi-line (3 rows), fixed at bottom in its own PanelNode
- **Tool Activity** panel appears inline between user message and response:
  - ✓ completed tools with name + duration (green)
  - ● in-progress tools with SpinnerNode (yellow)
  - Panel collapses when no tools are active
- **MCP status indicator** (bottom-right of status bar, reactive):
  - `✓ MCP (2/2)` = green — all servers connected
  - `⚠ MCP (1/2)` = yellow — degraded (auth required or warning)
  - `✗ MCP (0/2)` = red — server(s) unreachable
- **SpinnerSegment** shows while LLM is thinking; tokens stream in real-time
- **Session entity key**: `tui/{uuid}`, full actor system hosted in-process
- **MCP**: per-agent, not gateway-level

### Input Handling

- [Enter] sends the current input buffer as a user message
- Multi-line input supported (Shift+Enter or paste)
- Input buffer clears after send
- History scrollback not implemented in MVP

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
