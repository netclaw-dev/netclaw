# Netclaw Implementation Plan

Last updated: 2026-02-26
Mode: build

This file is RALPH-consumable.

Rules for loop execution:
- RALPH always picks the first task with unchecked `Done when` items.
- One iteration completes one task block.
- Task metadata must include PRD + OpenSpec references.
- Commits that complete a task must update this file and the referenced
  `openspec/changes/*/tasks.md` entries.

---

## Baseline (Complete)

Everything below is working today — no task checkboxes, just a status record.

**Actor infrastructure:** `LlmSessionActor` with persistence, turn loop,
compaction (tool result clearing + structured summarization), pre-compaction
memory flush, subscriber model with `OutputFilter` bitmask, snapshot strategy.
Session parent with `GenericChildPerEntityParent` routing via
`SessionMessageExtractor`. Protocol types (`SendUserMessage`, `TurnRecorded`,
`SessionCompacted`, etc.) with protobuf-net serialization.

**System prompt and personality:** Layered prompt assembly from `~/.netclaw/`
soul files (PERSONALITY.md, INSTRUCTIONS.md, USER.md) with project overlay
injection. `FileSystemPromptProvider` and `StaticSystemPromptProvider`.

**Tool framework:** `ToolRegistry` with MEAI `AITool` definitions, policy-filtered
loading, audit logging, agentic execution loop with parallel tool calls and
max-iterations circuit breaker. First-party tools: shell execution, file
read/write (source-generated schemas via Roslyn).

**Provider system:** Multi-provider config via `ChatClientFactory` with layered
config chain (netclaw.json + secrets.json + env vars). Ollama and OpenRouter
working. `NetclawChatClientProvider` resolves by model role.

**Slack adapter:** Socket Mode connection, event handling (`app_mention`,
`message`), entity key extraction (`{channelId}/{threadTs}`), reply delivery to
originating thread, reconnection on disconnect.

**Daemon + CLI split:** `Netclaw.Daemon` (Web SDK, Akka hosting, SignalR hub,
health probe) and `Netclaw.Cli` (Termina TUI, config commands). Mode routing in
`Program.cs`. Daemon management commands (`start`, `stop`, `status`, `install`,
`uninstall`). `SessionHub` with `CreateSession`, `SendMessage`, connection
lifecycle. Crash logging.

**TUI:** `ChatPage` with `StreamingTextNode`, `TextInputNode`, paste debounce,
scrolling. `ChatViewModel` with `SessionPipeline` (in-process prototype). Status
bar with model name, token usage, context percentage. Tool call spinners with
elapsed time.

**CLI commands:** `netclaw init` (stub), `netclaw doctor` (config validation,
autofix, status JSON output), `netclaw run` (daemon), `netclaw chat` (TUI).

**Research:** Context management patterns, agent soul/personality patterns,
actor-LLM optimization patterns, gateway architecture analysis.

---

## Milestone 1: Cloud Provider Access + Onboarding Wizard

**OpenSpec Changes:**
- `openspec/changes/oauth-first-provider-onboarding/`
- `openspec/changes/expand-mvp-for-autonomous-agent-vision/` (provider tasks)
- `openspec/changes/add-tui-adapter-and-config-hot-reload/` (wizard tasks)

**Goal:** Connect to cloud inference providers (OpenRouter, Anthropic, OpenAI) via
guided onboarding wizard with OAuth device flow and API key paths. Multi-provider
configuration from day one.

**Design reference:** `openspec/changes/oauth-first-provider-onboarding/design.md`
(includes concrete type definitions, state machine, back-navigation clearing
rules, provider capability matrix, and headless testing guidance).

**Provider capability matrix:**

| Provider    | Auth Methods               | Model Discovery | Notes                |
|-------------|----------------------------|-----------------|----------------------|
| Anthropic   | OAuth device flow, API key | Yes             | OAuth-first          |
| OpenAI      | OAuth device flow, API key | Yes             | OAuth-first          |
| OpenRouter  | API key only               | Yes             | No OAuth support     |
| Ollama      | None (local)               | Yes             | No auth required     |

### Phase A: Autonomous (RALPH-executable without operator credentials)

#### Task M1.A1: Provider config types and capability registry

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`
**OpenSpec:** `openspec/specs/netclaw-model-providers/spec.md`
**OpenSpec Tasks:** oauth-first-provider-onboarding 1.1
**Surface area:** `Netclaw.Configuration`
**Verification:** L2

Done when:
- [ ] `AuthMethod` enum added (`None`, `ApiKey`, `OAuthDevice`).
- [ ] `ModelDiscoverySource` enum added (`Live`, `Cache`, `Defaults`, `Manual`).
- [ ] `ProviderEntry` extended with `AuthMethod` property (default `None`), OAuth token fields (`OAuthAccessToken`, `OAuthRefreshToken`, `OAuthTokenExpiry`).
- [ ] `ModelReference` extended with `Provenance` property (`ModelDiscoverySource?`).
- [ ] `ProviderCapabilities` static class mapping provider type → supported auth methods and model discovery support.
- [ ] OAuth token fields bound from `secrets.json` overlay, not `netclaw.json`.

#### Task M1.A2: ChatClientFactory cloud provider cases

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`
**OpenSpec:** `openspec/specs/netclaw-model-providers/spec.md`
**OpenSpec Tasks:** expand-mvp 13.1
**Surface area:** `Netclaw.Daemon`
**Verification:** L2

Done when:
- [ ] `ChatClientFactory.Create()` switch handles `openrouter`, `anthropic`, `openai` provider types.
- [ ] Each provider creates appropriate `IChatClient` using MEAI-compatible SDK or HTTP client.
- [ ] Tests with fake/mock HTTP backends verify client creation for each provider type.

#### Task M1.A3: Init wizard scaffold (Termina)

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md` (CLI-010)
**OpenSpec:** `openspec/specs/netclaw-onboarding/spec.md`, `openspec/specs/netclaw-cli/spec.md`
**OpenSpec Tasks:** add-tui-adapter 3.1–3.9
**Surface area:** `Netclaw.Cli` TUI
**Verification:** L3
**Wireframe:** `docs/ui/TUI-001-command-wireframes.md` (netclaw init)

Done when:
- [ ] `InitCommand.cs` launches Termina wizard (replaces stub).
- [ ] `InitWizardPage.cs` with 6-step wizard layout (`PanelNode`, progress bar, step indicator).
- [ ] `InitWizardViewModel.cs` with step state machine and back-navigation (Esc goes back, preserves prior input).
- [ ] Step 1 (LLM provider) branches by provider type and auth method per design doc state machine.
- [ ] Back-navigation clearing rules: provider change clears auth + model; auth method change clears artifacts.
- [ ] Steps 2–5 (Slack, ACL, MCP, exposure) render with appropriate `TextInputNode`/`SelectionListNode` components.
- [ ] Step 6 (health check) runs validation probes with `SpinnerNode` → result indicator.
- [ ] Config written to `~/.netclaw/config/netclaw.json` and secrets to `~/.netclaw/config/secrets.json` on completion.

#### Task M1.A4: Headless wizard tests (VirtualTerminal)

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**OpenSpec:** `openspec/specs/netclaw-onboarding/spec.md`
**Surface area:** testing
**Verification:** L3

Done when:
- [ ] Tests use Termina `VirtualTerminal` + `VirtualInputSource` for headless wizard testing.
- [ ] Test: full wizard flow with Ollama (no auth) produces valid config file.
- [ ] Test: provider selection → back-navigation clears downstream state.
- [ ] Test: API key entry with masked input produces correct secrets.json.
- [ ] Test: health check step reports validation results.
- [ ] All tests use fake/mock provider backends (no live API calls).

#### Task M1.A5: Doctor config-shape checks

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**OpenSpec:** `openspec/specs/netclaw-cli/spec.md`
**OpenSpec Tasks:** oauth-first-provider-onboarding 4.1–4.3
**Surface area:** CLI diagnostics
**Verification:** L2

Done when:
- [ ] `netclaw doctor` validates provider entry has required fields for its auth method (API key present for `ApiKey`, OAuth tokens for `OAuthDevice`).
- [ ] Doctor checks model provenance and warns (exit 2) when model source is `Cache`, `Defaults`, or `Manual`.
- [ ] Doctor checks primary model provider is reachable (ping endpoint, verify auth).
- [ ] Remediation-first output for each failure with specific fix commands.

#### Task M1.A6: Model discovery fallback pipeline

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`
**OpenSpec:** `openspec/specs/netclaw-model-providers/spec.md`
**OpenSpec Tasks:** oauth-first-provider-onboarding 3.1–3.3
**Surface area:** provider integration
**Verification:** L2

Done when:
- [ ] Model discovery fallback order implemented: live catalog → cache → curated defaults → manual entry.
- [ ] Cached catalogs stored per provider type in `~/.netclaw/cache/models/`.
- [ ] `ModelDiscoverySource` provenance persisted with `ModelReference` in config.
- [ ] Tests with mocked discovery API verify fallback cascade and provenance tagging.

#### Task M1.A7: Provider fallback model failover

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`
**OpenSpec:** `openspec/specs/netclaw-model-providers/spec.md`
**OpenSpec Tasks:** expand-mvp 13.2, 13.4
**Surface area:** provider integration
**Verification:** L2

Done when:
- [ ] Primary + fallback model configuration with automatic failover on rate limit, timeout, or provider error.
- [ ] Failover logic in `NetclawChatClientProvider` or `IChatClient` decorator.
- [ ] Tests for provider switching and fallback activation with simulated failures.

### Phase B: Interactive (needs operator for real credentials)

#### Task M1.B1: OAuth device flow implementation

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`
**OpenSpec:** `openspec/specs/netclaw-model-providers/spec.md`
**OpenSpec Tasks:** oauth-first-provider-onboarding 2.1–2.3
**Surface area:** authentication
**Verification:** L2

Done when:
- [ ] OAuth device flow client for Anthropic (`start` → `show code` → `poll` → `success`/`denied`/`expired`/`cancel`).
- [ ] OAuth device flow client for OpenAI (same state machine, different endpoints).
- [ ] OAuth tokens persisted to `secrets.json` via secure config pipeline, redacted in all logs/output.
- [ ] Wizard Step 1b (OAuth) integrates device flow with Termina `SpinnerNode` for poll-wait state.

#### Task M1.B2: Live provider validation

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`
**OpenSpec:** `openspec/specs/netclaw-model-providers/spec.md`
**Surface area:** provider integration + onboarding
**Verification:** L2

Done when:
- [ ] API key validation against live endpoints (OpenRouter, Anthropic, OpenAI) during wizard and doctor.
- [ ] Live model catalog discovery from provider APIs populates `SelectionListNode` in wizard.
- [ ] Doctor verifies live provider reachability and auth validity.

#### Task M1.B3: Provider and model management commands (dual-mode)

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**OpenSpec:** `openspec/specs/netclaw-cli/spec.md`
**Wireframe:** `docs/ui/TUI-001-command-wireframes.md` (netclaw provider, netclaw model)
**Surface area:** CLI + TUI
**Verification:** L3

Done when:
- [ ] `netclaw provider` (bare) — Termina TUI guided setup reusing wizard Step 1 components (provider type, auth, credentials).
- [ ] `netclaw provider add` — single-shot: add with explicit `--name`, `--type`, `--auth-method` args.
- [ ] `netclaw provider list` — plain CLI: show configured providers, auth status.
- [ ] `netclaw provider remove` — plain CLI: remove provider (warn if model roles reference it).
- [ ] `netclaw model` (bare) — Termina TUI tree-based browser: providers → models, current role assignments, select to reassign.
- [ ] `netclaw model --role --provider --model` — single-shot: assign model to role directly.
- [ ] Model selector TUI component shared between `netclaw model` and `netclaw init` Step 1c.

#### Task M1.B4: End-to-end onboarding smoke test

**PRD:** all Milestone 1 PRDs
**OpenSpec Tasks:** oauth-first-provider-onboarding 5.1–5.3
**Surface area:** testing + docs
**Verification:** L2

Done when:
- [ ] End-to-end onboarding with real provider credentials verified manually.
- [ ] Doctor follow-up checks pass after successful onboarding.
- [ ] CLI/operator docs updated to reflect OAuth-first onboarding tree, provider management commands, and doctor follow-up checks.

---

## Milestone 2: MCP Support

**OpenSpec Changes:**
- `openspec/changes/expand-mvp-for-autonomous-agent-vision/` (MCP tasks, group 8)

**Goal:** Connect to MCP tool servers, discover tools, gate by policy.

### Task M2.1: MCP server profiles and tool discovery

**PRD:** `docs/prd/PRD-006-mcp-tool-integration.md`
**OpenSpec:** `openspec/specs/netclaw-mcp/spec.md`
**OpenSpec Tasks:** expand-mvp 8.1–8.2
**Surface area:** integration
**Verification:** L2

Done when:
- [ ] MCP server profiles (named, stdio/SSE transport, enable/disable) configurable in `netclaw.json`.
- [ ] Tool discovery at startup: connect to enabled servers, list tools, register as MEAI definitions.

### Task M2.2: Graceful degradation and validation

**PRD:** `docs/prd/PRD-006-mcp-tool-integration.md`
**OpenSpec:** `openspec/specs/netclaw-mcp/spec.md`
**OpenSpec Tasks:** expand-mvp 8.3–8.5
**Surface area:** integration
**Verification:** L2

Done when:
- [ ] Graceful degradation: unavailable server returns error, agent continues, reconnect on next call.
- [ ] MCP validation command (`netclaw mcp validate`).
- [ ] Tests for connection, discovery, policy gating, degradation.

### Task M2.3: Memorizer integration

**PRD:** `docs/prd/PRD-006-mcp-tool-integration.md`
**OpenSpec:** `openspec/specs/netclaw-mcp/spec.md`
**Surface area:** integration
**Verification:** L2

Done when:
- [ ] Memorizer store/search/get cycle works through session via MCP.
- [ ] MCP status indicator in TUI status bar (green/yellow/red).

---

## Milestone 3: Web Tools

**OpenSpec Changes:** TBD (may need new change)

**Goal:** Web search and web browsing capabilities.

### Task M3.1: Web search tool

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** `openspec/specs/netclaw-tools/spec.md`
**OpenSpec Tasks:** expand-mvp 7.1–7.3
**Surface area:** tools
**Verification:** L2

Done when:
- [ ] Web search tool implemented (evaluate: Brave Search API, SearXNG, Tavily).
- [ ] Configurable search backend selection via `config/netclaw.json`.
- [ ] Tests with mocked HTTP dependencies.

### Task M3.2: Web fetch/browse tool

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** `openspec/specs/netclaw-tools/spec.md`
**OpenSpec Tasks:** expand-mvp 7.4
**Surface area:** tools
**Verification:** L2

Done when:
- [ ] Web fetch tool (URL retrieval, HTML-to-text extraction, output truncation).
- [ ] Tests with mocked HTTP dependencies.

---

## Milestone 4: System Prompts + Personality

**OpenSpec Changes:**
- `openspec/changes/expand-mvp-for-autonomous-agent-vision/` (personality tasks, group 15)
- `openspec/changes/add-tui-adapter-and-config-hot-reload/` (bootstrap, section 6)

**Goal:** Onboarding-driven soul file creation, proper injection into sessions.

### Task M4.1: Conversational personality bootstrap

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`, `openspec/specs/netclaw-onboarding/spec.md`
**OpenSpec Tasks:** expand-mvp 15.1–15.3, add-tui-adapter 6.1–6.4
**Surface area:** onboarding
**Verification:** L2

Done when:
- [ ] First-run detection: trigger bootstrap when soul files don't exist on first `netclaw chat`.
- [ ] Bootstrap conversation: introduce, learn preferences, scan environment, write soul files, confirm.
- [ ] PERSONALITY.md, INSTRUCTIONS.md, USER.md written to config directory.
- [ ] Test: bootstrap triggers when files missing, skips when files exist.

### Task M4.2: System prompt assembly verification

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** agent personality
**Verification:** L2

Done when:
- [ ] Soul file CRUD through onboarding verified end-to-end.
- [ ] System prompt injection from soul files verified in session context.

---

## Milestone 5: Agent Memory System

**OpenSpec Changes:**
- `openspec/changes/expand-mvp-for-autonomous-agent-vision/` (memory tasks, group 9)
- Potentially new change for `IMemoryStore` interface

**Goal:** Persistent agent memory for cross-session knowledge retention.

**Design sketch — Four-Type Memory Framework:**

1. **Snapshot Memory** (MEMORY.md per agent) — curated current-state summary.
   Guaranteed retrieval. Written by `IMemoryExtractor` during pre-compaction
   flush (hook already exists, currently `NullMemoryExtractor`). High
   decision-density content promoted from session context before compaction
   clears it.

2. **Temporal Memory** (session logs via Akka.Persistence journal) — rolling
   window of recent events. Already exists. Enhancement: make post-compaction
   session summaries queryable, not just raw journal.

3. **Relational Memory** (identity/soul files) — hierarchical structure of who
   the agent is. Already exists in `~/.netclaw/soul/` (PERSONALITY.md,
   INSTRUCTIONS.md, USER.md). Expand with project AGENTS.md files and
   environment inventory. Guaranteed retrieval — load file, get file.

4. **Contextual Memory** (pluggable `IMemoryStore`) — probabilistic similarity
   retrieval for cross-session knowledge. Interface: store, search, get.
   First implementation: Memorizer via MCP (depends on Milestone 2).
   Future option: local SQLite+vector store to reduce external deps.

Key integration point: `IMemoryExtractor` fires during compaction and decides
what goes where — snapshot entries to MEMORY.md (guaranteed), contextual
entries to whatever `IMemoryStore` is configured.

Dependency chain: MCP support (M2) → Memorizer as first IMemoryStore →
memory extraction during compaction becomes real.

### Task M5.1: Replace NullMemoryExtractor with real implementation

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** agent memory
**Verification:** L2

Done when:
- [ ] `NullMemoryExtractor` replaced with implementation that writes MEMORY.md.
- [ ] Pre-compaction memory flush produces durable snapshot entries.
- [ ] Tests verify extraction and file write.

### Task M5.2: IMemoryStore interface and Memorizer adapter

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** agent memory
**Verification:** L2

Done when:
- [ ] `IMemoryStore` interface defined (store, search, get).
- [ ] Memorizer MCP adapter implemented as first `IMemoryStore` backend.
- [ ] Memory retrieval wired into session context injection.

### Task M5.3: Post-compaction summary persistence

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** agent memory
**Verification:** L2

Done when:
- [ ] Post-compaction session summaries persisted for temporal memory queries.
- [ ] Tests verify summary persistence and retrieval.

---

## Deferred (Not in MVP)

These capabilities are explicitly out of scope for the current plan. They may
be promoted to milestones in future planning sessions.

- **ACL/security enforcement** — tool grant limits, sender allowlists, default
  deny evaluation. Required before multi-user but not for single-user local.
  (OpenSpec: expand-mvp group 5)
- **Additional messaging channels** — Telegram, WhatsApp, Discord, webhooks.
- **Web UI / ops console** — PRD-003, deferred to post-MVP.
- **Config hot-reload** — `ConfigWatcherService` with `FileSystemWatcher`,
  debounce, validate-before-apply. (OpenSpec: add-tui-adapter section 5)
- **Scheduling system** — `ScheduleManagerActor`, cron/interval tasks, isolated
  execution. (OpenSpec: expand-mvp group 11)
- **Self-configuration through conversation** — agent modifies personality,
  project registry, environment via conversation. (OpenSpec: expand-mvp group 10)
- **CloudFlare/Tailscale tunnels** — external access for webhooks.
- **Multi-key entity routing** — full multi-pattern support and routing tests.
  (OpenSpec: expand-mvp group 3)
- **Local memory subsystem** — project registry, environment inventory,
  capability self-discovery. (OpenSpec: expand-mvp group 9)
- **SignalR thin client** — replace in-process `SessionPipeline` with SignalR
  client in CLI. (Task 1.28 from previous plan)
- **Daemon-required CLI commands** — session/tools/mcp/schedule/memory/acl
  queries via SignalR. (Task 1.30 from previous plan)

---

## Future Considerations

Patterns identified during implementation research that are deferred from
current milestones but should inform future design decisions. Full analysis in
the linked research documents.

### Near-Term (incorporate during active milestones)

- **Retry with exponential backoff** — `IChatClient` decorator or actor-level
  retry for transient LLM errors. Critical for scheduled task reliability.
  See: `docs/research/actor-llm-optimization-patterns.md` §5

### Medium-Term (post-MVP)

- **IChatClient decorator pipeline** — `CachingChatClient → RetryingChatClient
  → RateLimitingChatClient → ProviderChatClient`. Transparent to actor code.
  See: `docs/research/actor-llm-optimization-patterns.md` §1 (Tier 3)
- **Prompt cache warming** — Shared system prompt cache warmer actor.
  See: `docs/research/actor-llm-optimization-patterns.md` §1 (Tier 1)
- **Cache-aware compaction** — Anthropic cache control breakpoints on
  system prompt and compaction summary boundaries.
  See: `docs/research/actor-llm-optimization-patterns.md` §1 (Tier 2)

### Long-Term

- **Sub-agent isolation** — Child task actors with independent context
  windows. Architecture already supports it (`SessionState` is decoupled).
  See: `docs/research/actor-llm-optimization-patterns.md` §6

### Research Documents

- `docs/research/context-management-patterns.md` — Cross-SDK compaction
  and memory patterns
- `docs/research/agent-patterns.md` — Agent soul, personality, tooling,
  and onboarding patterns from comparable projects
- `docs/research/actor-llm-optimization-patterns.md` — Prompt caching,
  safety circuit breakers, parallel execution, streaming, retry, and
  sub-agent isolation patterns
- `docs/research/agent-gateway-architecture.md` — Architecture analysis
  (OpenClaw, IronClaw, PicoClaw). Informed the daemon + thin client split.
