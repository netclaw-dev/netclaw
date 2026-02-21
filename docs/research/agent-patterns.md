# Agent Architecture Patterns Research

Date: 2026-02-21
Task: 0.5.1 (IMPLEMENTATION_PLAN.md)

Research across OpenClaw, IronClaw, ZeroClaw, PicoClaw, Goose, Claude Code,
Aider, Continue.dev, and Open Interpreter to identify patterns for Netclaw's
agent soul, memory, tooling, onboarding, and scheduling systems.

---

## 1. Agent Soul / Personality

### The Emerging Standard: Separation of Concerns

The OpenClaw ecosystem established a de facto standard that separates agent
identity into distinct markdown files, each with a single responsibility:

| File | Purpose | Analogy |
|------|---------|---------|
| `SOUL.md` | Values, personality, tone, boundaries | "Why" — who the agent *is* |
| `IDENTITY.md` | Name, capabilities, metadata, version | "Who" — what it can do |
| `AGENTS.md` | Operating instructions, workflows, rules | "How" — behavioral guidelines |
| `USER.md` | Owner preferences, timezone, how to address them | "For whom" |
| `TOOLS.md` | Available tool documentation/guidance | "With what" |
| `HEARTBEAT.md` | Periodic task checklist | "When" (proactive) |

This pattern is used by OpenClaw, IronClaw, ZeroClaw, and PicoClaw. Claude
Code uses a simpler model (single CLAUDE.md + `.claude/rules/` directory).

### System Prompt Layering (Universal Pattern)

Every tool surveyed uses a layered system prompt model:

```
Layer 1: Core system message (hardcoded by the tool)
Layer 2: User-global configuration (~/.tool/config)
Layer 3: Project-level instructions (repo root files)
Layer 4: Path/directory-specific rules (loaded on demand)
Layer 5: Session-specific context (conversation, tool results)
```

Later layers augment or override earlier layers. This is consistent across
Claude Code, Goose, and the OpenClaw family.

### Key Insight: Soul Is Data, Not Code

IronClaw made this explicit: the personality is stored as data (markdown files
in a workspace database), not as code. The agent "reconstructs itself from
files on every boot." This makes identity hot-swappable and version-
controllable without code changes.

### Recommendation for Netclaw

Adopt a simplified version of the OpenClaw file structure, adapted for
Netclaw's actor-based architecture:

```
~/.netclaw/
  soul/
    PERSONALITY.md     # Agent character, tone, values
    INSTRUCTIONS.md    # Operating rules and behavioral guidelines
    USER.md            # Owner preferences and context
  projects/            # Registered project configurations
  environment/         # Capability inventory and tool availability
  schedules/           # Persisted scheduled tasks
```

These files get loaded into the LLM context at session start. The session
actor caches them; a session reboot refreshes the context.

---

## 2. Memory Architecture

### Three Dominant Patterns

**Pattern A: Pure file-based (markdown)**
- Used by: Claude Code, PicoClaw, OpenClaw (base)
- Pros: Simple, portable, human-readable, version-controllable
- Cons: No semantic search, doesn't scale past ~100KB of notes
- How: Markdown files on disk, loaded into context at session start

**Pattern B: Hybrid SQLite + file**
- Used by: ZeroClaw, OpenClaw (advanced)
- Pros: Semantic search via vector embeddings + exact keyword via FTS5/BM25
- Cons: Requires embedding provider, more complex
- How: SQLite with FTS5 + vector BLOBs, configurable weights (default 0.7
  vector / 0.3 keyword)

**Pattern C: Database + hybrid search**
- Used by: IronClaw (PostgreSQL + pgvector)
- Pros: Full Reciprocal Rank Fusion (RRF), memory hygiene (auto-cleanup),
  production-grade search
- Cons: Requires PostgreSQL, more infrastructure
- How: `score(d) = sum(1 / (k + rank(d)))` across FTS and vector results

### OpenClaw's Memory Lifecycle (Best-in-Class)

1. Agent appends notes to `memory/YYYY-MM-DD.md` (daily logs)
2. Important facts get curated into `MEMORY.md` (long-term, evergreen)
3. Markdown changes trigger async embedding updates
4. Queries use `memory_search` (semantic) or `memory_get` (targeted read)
5. **Pre-compaction flush**: silent agentic turn saves durable memories before
   context window resets
6. Temporal decay ensures recent notes rank higher (30-day half-life)
7. MMR re-ranking prevents redundant results

### Pre-Compaction Memory Flush (Critical Pattern)

OpenClaw triggers a silent agentic turn when context approaches compaction
threshold. The model is prompted to write durable memories to disk before
the context resets. This directly counters "context rot" — the #1 complaint
about LLM assistants.

```json5
memoryFlush: {
  enabled: true,
  softThresholdTokens: 4000,
  systemPrompt: "Session nearing compaction. Store durable memories now."
}
```

### IronClaw's Memory Hygiene

Automatic cleanup of stale workspace documents runs on heartbeat ticks.
Identity files (SOUL.md, IDENTITY.md, etc.) are exempt from cleanup.
Configurable retention (default 30 days) and cadence (default 12 hours).

### Research Consensus

The field is converging on hybrid architectures that combine file-based
curated memory with vector/graph search for retrieval over larger corpora.
Key insight: **indiscriminate memory storage degrades performance** — active
pruning and curation yields up to 10% performance gains over naive strategies.

### Recommendation for Netclaw

**Start with Pattern A (files), design for Pattern B (hybrid SQLite):**

- MVP: Markdown files on disk for soul, projects, environment inventory
- The pre-compaction flush pattern is essential — implement from day one
- Netclaw already has PostgreSQL for Akka.Persistence journal/snapshots; could
  add a memory table with pgvector later if SQLite is insufficient
- MCP/Memorizer serves as the "Pattern C" tier for research and knowledge base
- Keep local memory small and personal (file paths, tool inventory, schedules)
- Delegate large-corpus memory to Memorizer via MCP

---

## 3. Tooling Configuration

### Tool Discovery Patterns

**Built-in registry** (OpenClaw, IronClaw):
- Tools registered at startup, exposed as both system prompt text and
  structured function definitions
- Allow/deny lists control which tools are available per session
- Tool groups provide shorthands (`group:fs`, `group:web`, etc.)

**MCP-first** (Goose, Continue.dev):
- All tool capabilities come from MCP server connections
- Extensions are configured in YAML, spawned as MCP server processes
- Natural discovery via MCP capability negotiation

**Protected tool names** (IronClaw):
- A hardcoded set of ~30 built-in tool names that cannot be shadowed by
  dynamic registrations. Prevents malicious WASM tools from replacing
  `shell` or `memory_write`.

### IronClaw's Three-Tier Tool System

| Tier | Description | Security |
|------|-------------|----------|
| Built-in | Native Rust tools | Full trust |
| WASM | Sandboxed WebAssembly modules | Capability-based permissions |
| MCP | External protocol servers | OAuth 2.1, rate limiting |

WASM tools run in isolated containers with explicit opt-in for HTTP, secrets,
and tool invocation. Credential injection happens at the host boundary —
API keys are never exposed to WASM code.

### Conversational Tool Installation (IronClaw)

```
User: "add notion"
  -> tool_search("notion")      -> finds MCP server in registry
  -> tool_install("notion")     -> saves config
  -> tool_auth("notion")        -> OAuth flow, returns URL
  -> tool_activate("notion")    -> connects, registers tools
```

### Recommendation for Netclaw

- Use `Microsoft.Extensions.AI` tool abstraction as the built-in tier
- MCP for external tools (Memorizer, future integrations)
- Capability self-discovery for system tools (git, claude, opencode, dotnet)
- Allow/deny lists per session or per channel instruction set
- No WASM sandbox needed for MVP (single-user, trusted environment)

---

## 4. Onboarding

### OpenClaw's Two-Phase Onboarding

**Phase 1: Technical wizard** (`openclaw onboard`, 7 steps):
1. Model/auth selection
2. Workspace setup + bootstrap file seeding
3. Gateway configuration (port, auth, Tailscale)
4. Channel integration (WhatsApp, Telegram, Discord, Slack, etc.)
5. Daemon installation (systemd/LaunchAgent)
6. Health verification
7. Skills installation

**Phase 2: Personality bootstrap** (first conversation):
1. User sends: "Hey, let's get you set up. Read BOOTSTRAP.md."
2. Agent runs a short Q&A ritual (one question at a time)
3. Writes identity and preferences to IDENTITY.md, USER.md, SOUL.md
4. Deletes BOOTSTRAP.md so it only runs once

### IronClaw's Onboarding (8 steps)

1. Database connection + migrations
2. Security (master key generation, AES-256-GCM)
3. Inference provider selection
4. Model selection
5. Embeddings configuration
6. Channel configuration (CLI, HTTP, Telegram, Tunnel, WASM)
7. Extension installation
8. Heartbeat configuration

### Recommendation for Netclaw

**Two-phase onboarding aligned with Netclaw's architecture:**

Phase 1 — CLI wizard (`netclaw init`):
1. LLM provider configuration (OpenRouter API key, model selection)
2. Slack app setup (bot token, app token for Socket Mode)
3. PostgreSQL connection string
4. ACL bootstrap (owner identity, initial channel rules)
5. Exposure mode (local-only, Tailscale Serve, Cloudflare Tunnel)
6. Health check (verify Slack connection, DB connection, LLM reachability)

Phase 2 — Conversational personality setup (first Slack message):
1. "Hi, I'm Netclaw. Let me learn about you and your setup."
2. Ask about projects to register (repo paths)
3. Discover environment capabilities (scan for installed tools)
4. Write PERSONALITY.md, USER.md, environment inventory
5. Confirm readiness

---

## 5. Scheduling

### OpenClaw's Cron System

Three schedule types: one-shot (`at`), fixed interval (`every`), cron
expressions. Jobs persist to `~/.openclaw/cron/jobs.json`. Gateway-owned
(not model-owned). Execution modes: main session (inline) or isolated
(fresh session per run). Exponential retry backoff. Delivery modes: announce
to channel, webhook POST, or internal-only.

### IronClaw's Routines Engine (Most Sophisticated)

**Four trigger types:**
- `Cron` — fire on a cron schedule
- `Event` — fire when a channel message matches a regex pattern
- `Webhook` — fire on incoming POST to `/hooks/routine/{id}`
- `Manual` — fire only via tool call or CLI

**Execution modes:**
- Lightweight — single LLM call, no scheduler slot
- Full job — delegated to scheduler with isolated context

**Guardrails:**
- Cooldown periods between fires
- Max concurrent runs per routine
- Global max concurrent routines
- Consecutive failure tracking
- Per-routine state (JSON blob persisted in DB)

### PicoClaw's Heartbeat Pattern

`HEARTBEAT.md` is checked every 30 minutes. The agent reads the checklist,
processes any items that need attention, and reports findings to the
configured channel. If nothing needs attention, it replies `"HEARTBEAT_OK"`
and no message is sent.

### Recommendation for Netclaw

Adopt IronClaw's routines model, simplified for MVP:

- **Cron triggers** for scheduled tasks (chat-driven: "check this every 6h")
- **Event triggers** for ambient channel monitoring (post-MVP)
- **Webhook triggers** for external integrations (post-MVP)
- Persist schedules as files on disk (JSON), loaded at startup
- Akka scheduler/timers for execution (natural fit for actor system)
- Each scheduled task execution creates a new session or runs in a dedicated
  scheduling actor
- Heartbeat system for proactive periodic checks (simple: check HEARTBEAT.md
  every N minutes)

---

## 6. Configuration Model

### Comparison

| Tool | Format | Hot-reload | Self-modifiable |
|------|--------|------------|-----------------|
| OpenClaw | JSON5 | Yes (hybrid mode) | Yes (via CLI/UI/chat) |
| IronClaw | TOML + DB | DB changes immediate | Yes (via tools) |
| ZeroClaw | TOML | No | No |
| Claude Code | Markdown | On session start | Yes (via conversation) |
| Goose | YAML | No | No |

### Key Insight: Markdown for Behavior, Structured Data for Config

Every tool uses markdown for behavioral instructions (system prompts, rules,
personality) and a structured format (YAML/TOML/JSON) for technical
configuration (model selection, API keys, tool registration, scheduling).

### Recommendation for Netclaw

- **Markdown** for soul files (PERSONALITY.md, INSTRUCTIONS.md, USER.md)
- **JSON** for structured config (projects registry, environment inventory,
  scheduled tasks, channel instructions, ACL rules)
- All files on disk under `~/.netclaw/` or a configured data directory
- Bot can modify its own config files through conversation
- Session reboot to refresh (config cached in LLM context)
- Future web UI reads same files

---

## 7. Patterns to Adopt

### Must-Have (MVP)

1. **Separated soul files** — PERSONALITY.md, INSTRUCTIONS.md, USER.md
2. **Pre-compaction memory flush** — save durable memories before context resets
3. **Layered system prompt** — global personality + project AGENTS.md overlays
4. **Capability self-discovery** — scan environment for installed tools at
   startup and on demand
5. **Chat-driven scheduling** — cron triggers persisted as JSON, executed by
   Akka timers
6. **Self-configuration** — bot modifies its own config files through
   conversation
7. **Project registry** — explicit registration with repo path, AGENTS.md
   location, and associated channels

### Should-Have (Phase 2)

1. **Event triggers** — ambient channel monitoring with regex pattern matching
2. **Heartbeat system** — periodic proactive check-in
3. **Onboarding wizard** — CLI setup + conversational personality bootstrap
4. **Memory hygiene** — auto-cleanup of stale daily logs (IronClaw pattern)

### Nice-to-Have (Later)

1. **Hybrid search** — SQLite FTS5 + vector embeddings for local memory
2. **Webhook triggers** — external integrations via Tailscale/CF Tunnel
3. **Hot-reload config** — apply config changes without full restart
4. **Protected tool names** — prevent shadowing of built-in tools

### Patterns to Reject

1. **WASM sandbox** — unnecessary for single-user trusted environment
2. **Separate IDENTITY.md file** — merge into PERSONALITY.md for simplicity
3. **Multiple embedding providers** — start with one (OpenRouter or Ollama)
4. **BOOTSTRAP.md self-destruct** — cute but unnecessary; onboarding can be
   a CLI flag or first-run detection

---

## Sources

### Primary Research

- [OpenClaw](https://github.com/openclaw/openclaw) — 68K stars, TypeScript,
  the ecosystem standard
- [IronClaw (NEAR AI)](https://github.com/nearai/ironclaw) — Rust,
  security-first with WASM sandbox
- [ZeroClaw](https://github.com/zeroclaw-labs/zeroclaw) — Rust, 3.4MB binary,
  <5MB RAM
- [PicoClaw](https://github.com/sipeed/picoclaw) — most comprehensive
  file-based identity system
- [Goose (Block)](https://github.com/block/goose) — MCP-first, recipes +
  scheduling
- [Claude Code](https://code.claude.com) — CLAUDE.md, .claude/rules/,
  project memory
- [Aider](https://aider.chat) — repo map, CONVENTIONS.md
- [Continue.dev](https://docs.continue.dev) — .continuerules, MCP tools
- [Open Interpreter](https://github.com/openinterpreter/open-interpreter) —
  code execution focus

### Documentation

- [OpenClaw Docs](https://docs.openclaw.ai/) — system prompt, memory, tools,
  cron, configuration
- [IronClaw DeepWiki](https://deepwiki.com/nearai/ironclaw)
- [ZeroClaw DeepWiki](https://deepwiki.com/zeroclaw-labs/zeroclaw)

### Research Papers and Articles

- [Design Patterns for Long-Term Memory in LLM-Powered Architectures](https://serokell.io/blog/design-patterns-for-long-term-memory-in-llm-powered-architectures)
- [Making Sense of Memory in AI Agents](https://www.leoniemonigatti.com/blog/memory-in-ai-agents.html)
- [Comparing Memory Systems for LLM Agents](https://www.marktechpost.com/2025/11/10/comparing-memory-systems-for-llm-agents-vector-graph-and-event-logs/)
