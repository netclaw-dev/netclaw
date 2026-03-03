# Netclaw Implementation Plan

Last updated: 2026-03-03
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
identity files (SOUL.md, AGENTS.md, TOOLING.md) with dynamic context layers
(tool index, skill index, memory index). `FileSystemPromptProvider` and
`StaticSystemPromptProvider`.

**Tool framework:** `ToolRegistry` with MEAI `AITool` definitions, policy-filtered
loading, audit logging, agentic execution loop with parallel tool calls and
max-iterations circuit breaker. First-party tools: shell execution, file
read/write (source-generated schemas via Roslyn).

**Provider system:** Multi-provider config via `ChatClientFactory` with layered
config chain (netclaw.json + secrets.json + env vars). Ollama and OpenRouter
working. `NetclawChatClientProvider` resolves by model role.
`OpenRouterReasoningExcludePolicy` prevents SDK deserialization failures from
non-standard reasoning fields. Provider endpoint defaults resolved per provider
type via `ProviderCapabilities` (fixes config-omitted endpoints hitting Ollama).
Error detail now flows through `ErrorOutput` DTO for client-side diagnostics.

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
- [x] `AuthMethod` enum added (`None`, `ApiKey`, `OAuthDevice`).
- [x] `ModelDiscoverySource` enum added (`Live`, `Defaults`, `Manual`).
- [x] `ProviderEntry` extended with `AuthMethod` property (default `None`), OAuth token fields (`OAuthAccessToken`, `OAuthRefreshToken`, `OAuthTokenExpiry`).
- [x] `ModelReference` extended with `Provenance` property (`ModelDiscoverySource?`).
- [x] `ProviderCapabilities` static class mapping provider type → supported auth methods and model discovery support.
- [x] OAuth token fields bound from `secrets.json` overlay, not `netclaw.json`.

#### Task M1.A2: ChatClientFactory cloud provider cases

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`
**OpenSpec:** `openspec/specs/netclaw-model-providers/spec.md`
**OpenSpec Tasks:** expand-mvp 13.1
**Surface area:** `Netclaw.Daemon`
**Verification:** L2

Done when:
- [x] `ChatClientFactory.Create()` switch handles `openrouter`, `anthropic`, `openai` provider types.
- [x] Each provider creates appropriate `IChatClient` using MEAI-compatible SDK or HTTP client.
- [x] Tests with fake/mock HTTP backends verify client creation for each provider type.

#### Task M1.A3: Init wizard scaffold (Termina)

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md` (CLI-010)
**OpenSpec:** `openspec/specs/netclaw-onboarding/spec.md`, `openspec/specs/netclaw-cli/spec.md`
**OpenSpec Tasks:** add-tui-adapter 3.1–3.9
**Surface area:** `Netclaw.Cli` TUI
**Verification:** L3
**Wireframe:** `docs/ui/TUI-001-command-wireframes.md` (netclaw init)

Done when:
- [x] `InitCommand.cs` launches Termina wizard (replaces stub).
- [x] `InitWizardPage.cs` with 5-step wizard layout (`PanelNode`, progress bar, step indicator). *(Reduced from 6 steps — MCP moved to separate CLI config.)*
- [x] `InitWizardViewModel.cs` with step state machine and back-navigation (Esc goes back, preserves prior input).
- [x] Step 1 (LLM provider) branches by provider type and auth method per design doc state machine.
- [x] Back-navigation clearing rules: provider change clears auth + model; auth method change clears artifacts.
- [x] Steps 2–4 (Slack/ChatServices, ACL, exposure) render with appropriate `TextInputNode`/`SelectionListNode` components. *(MCP step deferred to CLI config.)*
- [x] Step 5 (health check) runs validation probes with `SpinnerNode` → result indicator.
- [x] Config written to `~/.netclaw/config/netclaw.json` and secrets to `~/.netclaw/config/secrets.json` on completion.

#### Task M1.A4: Headless wizard tests (VirtualTerminal)

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**OpenSpec:** `openspec/specs/netclaw-onboarding/spec.md`
**Surface area:** testing
**Verification:** L3

Done when:
- [ ] Tests use Termina `VirtualTerminal` + `VirtualInputSource` for headless wizard testing.
- [x] Test: full wizard flow with Ollama (no auth) produces valid config file. *(ViewModel-level test via `InitWizardViewModelTests`)*
- [x] Test: provider selection → back-navigation clears downstream state.
- [x] Test: API key entry with masked input produces correct secrets.json.
- [x] Test: health check step reports validation results.
- [x] All tests use fake/mock provider backends (no live API calls).

#### Task M1.A5: Doctor config-shape checks

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**OpenSpec:** `openspec/specs/netclaw-cli/spec.md`
**OpenSpec Tasks:** oauth-first-provider-onboarding 4.1–4.3
**Surface area:** CLI diagnostics
**Verification:** L2

Done when:
- [ ] `netclaw doctor` validates provider entry has required fields for its auth method (API key present for `ApiKey`, OAuth tokens for `OAuthDevice`).
- [ ] Doctor checks model provenance and warns (exit 2) when model source is `Defaults` or `Manual`.
- [ ] Doctor checks primary model provider is reachable (ping endpoint, verify auth).
- [ ] Remediation-first output for each failure with specific fix commands.

#### Task M1.A6: Model discovery fallback pipeline

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`
**OpenSpec:** `openspec/specs/netclaw-model-providers/spec.md`
**OpenSpec Tasks:** oauth-first-provider-onboarding 3.1–3.3
**Surface area:** provider integration
**Verification:** L2

Done when:
- [ ] Model discovery fallback order implemented: live catalog → curated defaults → manual entry. *(Live catalog + manual entry working; curated defaults not yet implemented.)*
- [ ] `ModelDiscoverySource` provenance persisted with `ModelReference` in config. *(Property exists but not set during config write.)*
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
- `openspec/changes/archive/2026-03-03-expand-mvp-for-autonomous-agent-vision/` (MCP tasks, archived)

**Goal:** Connect to MCP tool servers, discover tools, gate by policy.

### Task M2.1: MCP server profiles and tool discovery

**PRD:** `docs/prd/PRD-006-mcp-tool-integration.md`
**OpenSpec:** `openspec/specs/netclaw-mcp/spec.md`
**OpenSpec Tasks:** expand-mvp 8.1–8.2
**Surface area:** integration
**Verification:** L2

Done when:
- [x] MCP server profiles (named, stdio/SSE transport, enable/disable) configurable in `netclaw.json`.
- [x] Tool discovery at startup: connect to enabled servers, list tools, register as MEAI definitions.

### Task M2.2: Graceful degradation and validation

**PRD:** `docs/prd/PRD-006-mcp-tool-integration.md`
**OpenSpec:** `openspec/specs/netclaw-mcp/spec.md`
**OpenSpec Tasks:** expand-mvp 8.3–8.5
**Surface area:** integration
**Verification:** L2

Done when:
- [x] Graceful degradation: unavailable server returns error, agent continues, reconnect on next call.
- [x] MCP validation covered by `netclaw doctor` (`McpServersDoctorCheck`) and `netclaw mcp list` (live probe status). *(Standalone `mcp validate` command pruned — redundant surface area.)*
- [x] Tests for connection, discovery, policy gating, degradation.

### Task M2.3: Memorizer integration

**PRD:** `docs/prd/PRD-006-mcp-tool-integration.md`
**OpenSpec:** `openspec/specs/netclaw-mcp/spec.md`
**Surface area:** integration
**Verification:** L2

Done when:
- [x] Memorizer store/search/get cycle works through session via MCP. *(4-tool surface: `find_memories`, `get_memories`, `store_memory`, `update_memory`. `store_memory` via subagent delegation, others via direct MCP pass-through.)*
- [ ] MCP status indicator in TUI status bar (green/yellow/red).

---

## Milestone 3: Web Tools

**OpenSpec Changes:**
- `openspec/changes/archive/2026-03-01-search-provider-abstraction/`

**Goal:** Web search and web browsing capabilities.

### Task M3.1: Web search tool

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** `openspec/specs/netclaw-tools/spec.md`, `openspec/specs/netclaw-search/spec.md`
**Surface area:** tools (`Netclaw.Search` project)
**Verification:** L2

Done when:
- [x] Web search tool implemented with 3 backends: Brave Search API, SearXNG, DuckDuckGo. *(Implemented as `WebSearchTool` with `SearchConfig` provider abstraction in `Netclaw.Search` project.)*
- [x] Configurable search backend selection via `config/netclaw.json` (`Search.Provider` setting).
- [x] Tests with mocked HTTP dependencies.

### Task M3.2: Web fetch/browse tool

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** `openspec/specs/netclaw-tools/spec.md`
**Surface area:** tools
**Verification:** L2

Done when:
- [x] Web fetch tool (URL retrieval, HTML-to-text extraction, output truncation). *(Implemented as `WebFetchTool`.)*
- [x] Tests with mocked HTTP dependencies.

---

## Milestone 4: System Prompts + Personality

**OpenSpec Changes:**
- `openspec/changes/archive/2026-03-03-expand-mvp-for-autonomous-agent-vision/` (archived)

**Goal:** Onboarding-driven identity file creation, proper injection into sessions.

### Task M4.1: Wizard-based personality bootstrap

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`, `openspec/specs/netclaw-onboarding/spec.md`
**Surface area:** onboarding
**Verification:** L2

Done when:
- [x] Init wizard Identity step (step 8 of 9) collects agent name and communication style.
- [x] `WriteIdentityFiles()` writes `SOUL.md`, `AGENTS.md`, `TOOLING.md` to `~/.netclaw/identity/`.
- [x] `identity-management` system skill provides triage guidance for identity file content.

> _Design note: conversational bootstrap was replaced with wizard-based bootstrap.
> The agent refines personality through conversation using `file_write` on identity
> files, guided by the `identity-management` skill._

### Task M4.2: System prompt assembly verification

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** agent personality
**Verification:** L2

Done when:
- [x] Layered prompt assembly from identity files with dynamic context layers (tool index, skill index, memory index).
- [x] `FileSystemPromptProvider` loads identity files at session start.

---

## Milestone 5: Agent Memory System

**OpenSpec Changes:**
- `openspec/changes/unified-memory-provider/` (tasks 3.2–3.4 remaining)
- `openspec/changes/archive/2026-03-03-operationalize-subagent-core/` (complete)

**Goal:** Persistent agent memory for cross-session knowledge retention with
pluggable backends (file-based and Memorizer).

**Architecture (implemented):**

Two memory backends behind a unified 4-tool surface (`find_memories`,
`get_memories`, `store_memory`, `update_memory`). No shared `IMemoryProvider`
abstraction — each backend has dedicated tool implementations. Backend
selection via `Memory.Provider` in `netclaw.json` (`"files"` default,
`"memorizer"` optional).

- **File backend:** `FileMemoryStore` manages `~/.netclaw/memories/` with
  individual `.md` files, YAML front matter, `memory.md` index. Thread-safe
  via `SemaphoreSlim` with in-memory cache. Multi-level scoring for search.
- **Memorizer backend:** `store_memory` spawns `memory-curator` subagent via
  `SubAgentActor` (10–30s, handles dedup/routing/linking). `find_memories`,
  `get_memories`, `update_memory` are fast MCP pass-throughs.
- **Context layer:** `MemoryIndexContextLayer` with 3 states (FileBacked,
  MemorizerConnected, MemorizerDisconnected). Teaches two-phase retrieval.
- **Extractors:** `FileMemoryExtractor` and `MemorizerMemoryExtractor`
  implement `IMemoryExtractor` for pre-compaction memory flush.
- **Wiring:** `ToolIndexUpdater` determines backend after MCP discovery,
  registers tools, updates context layer.

### Task M5.1: File-backed memory store and 4-tool surface

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** `Netclaw.Actors.Memory`
**Verification:** L2

Done when:
- [x] `FileMemoryStore` with CRUD: `StoreAsync`, `SearchAsync` (multi-level scoring), `GetByIdsAsync`, `EditAsync`, `DeleteAsync`.
- [x] File-backed tools: `FileFindMemoriesTool`, `FileGetMemoriesTool`, `StoreMemoryTool`, `FileUpdateMemoryTool`.
- [x] `memory.md` auto-generated index table, updated on every write.
- [x] `MemoryConfig` with `Provider` selection (`"files"` / `"memorizer"`).
- [x] Tests for all store operations and tool behaviors.

### Task M5.2: Memorizer backend with subagent delegation

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`, `openspec/specs/netclaw-subagents/spec.md`
**Surface area:** `Netclaw.Actors.Memory`, `Netclaw.Actors.SubAgents`
**Verification:** L2

Done when:
- [x] `MemorizerStoreMemoryTool` spawns `memory-curator` subagent with 8 Memorizer MCP tools.
- [x] `MemorizerFindMemoriesTool`, `MemorizerGetMemoriesTool`, `MemorizerUpdateMemoryTool` as MCP pass-throughs.
- [x] `SubAgentActor` — ephemeral actor with autonomous tool loop, max 10 iterations, wall-clock timeout.
- [x] `SubAgentConfig` with configurable timeouts (store=180s, search=30s, default=60s).
- [x] `SubAgentOutput` observability events via `ToolExecutionContext.OnSubAgentActivity`.
- [x] Tests for subagent lifecycle, MCP delegation, timeout, disconnected fallback.

### Task M5.3: Memory extractors and context layer

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** `Netclaw.Actors.Memory`, `Netclaw.Configuration`
**Verification:** L2

Done when:
- [x] `FileMemoryExtractor` implements `IMemoryExtractor` — saves to `FileMemoryStore` with `["extraction", "compaction"]` tags.
- [x] `MemorizerMemoryExtractor` implements `IMemoryExtractor` — saves via `memorizer/store` MCP, graceful no-op when disconnected.
- [x] `MemoryIndexContextLayer` with 3-state content (FileBacked, MemorizerConnected, MemorizerDisconnected).
- [x] `ToolIndexUpdater` wires correct backend tools after MCP discovery.
- [x] Init wizard memory step (step 6 of 9), connectivity probe, fallback to files.

### Task M5.4: System skills for memory guidance

**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** system skills
**Verification:** L1

Done when:
- [x] `memory-usage/1.1.0.md` — 4-tool surface, two-phase retrieval, update/delete, backend notes.
- [x] `memorizer-usage/1.1.0.md` — subagent delegation, two-tier model, advanced ops.
- [x] Embedded copies in `src/Netclaw.Daemon/BuiltInSkills/` in sync.
- [x] Manifest regenerated.

### Task M5.5: Diagnostics and integration (remaining)

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**OpenSpec:** `openspec/specs/netclaw-cli/spec.md`
**OpenSpec Tasks:** unified-memory-provider 3.2–3.4
**Surface area:** CLI diagnostics
**Verification:** L2

Done when:
- [x] ~~`MemoryDoctorCheck`~~ — dropped (low value; file backend auto-creates directory, Memorizer connectivity covered by `McpServersDoctorCheck`).
- [x] Memory line in `netclaw status` output — provider, health, backend-specific details.
- [x] Integration test: store → find → get → edit → delete round-trip via tool wrappers over real `FileMemoryStore`.

### Task M5.6: Post-compaction summary persistence (deferred)

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** agent memory
**Verification:** L2

Done when:
- [ ] Post-compaction session summaries queryable (not just raw journal).
- [ ] Tests verify summary persistence and retrieval.

---

## Deferred (Not in MVP)

These capabilities are explicitly out of scope for the current plan. They may
be promoted to milestones in future planning sessions.

- **ACL/security enforcement** — tool grant limits, sender allowlists, default
  deny evaluation. Required before multi-user but not for single-user local.
- **Additional messaging channels** — Telegram, WhatsApp, Discord, webhooks.
- **Web UI / ops console** — PRD-003, deferred to post-MVP.
- **Config hot-reload** — `ConfigWatcherService` with `FileSystemWatcher`,
  debounce, validate-before-apply.
- **Scheduling system** — `ScheduleManagerActor`, cron/interval tasks, isolated
  execution.
- **Self-configuration through conversation** — agent modifies personality,
  project registry, environment via conversation.
- **CloudFlare/Tailscale tunnels** — external access for webhooks.
- **Multi-key entity routing** — full multi-pattern support and routing tests.
- **Local memory subsystem** — project registry, environment inventory,
  capability self-discovery.
- **SignalR thin client** — replace in-process `SessionPipeline` with SignalR
  client in CLI.
- **Daemon-required CLI commands** — session/tools/mcp/schedule/memory/acl
  queries via SignalR.

### Subagent Roadmap (from PR #102)

These items build on the `SubAgentActor` infrastructure landed in PR #102.

- **Disk-based subagent definitions** — `~/.netclaw/agents/{name}.json` with
  system prompt, tool allowlist, model role, timeout. Near-term post-MVP.
  Prerequisite for `spawn_agent` tool and subagent discovery context layer.
- **`spawn_agent` tool** — User-facing delegation tool. Operator or frontline
  model can spawn named subagents for specialized tasks. Prerequisite:
  disk-based subagent definitions. Natural entry when Phase 3 (delegated
  coding) begins.
- **Subagent discovery context layer** — General specialist catalog in context
  layer (extends MCP shadow catalog pattern to subagents). Lists available
  subagent definitions with capabilities. Pairs with disk-based definitions.
- **Memory storage quality gate** — Pre-store validation for thin/hallucinated
  memories before `store_memory` persists. Options: curator prompt enrichment,
  confidence scoring, source citation requirement. Applies to both file and
  Memorizer backends.
- **Multi-turn subagent sessions** — Phase 3 (delegated coding). Current
  `SubAgentActor` is single-loop; multi-turn needed for spawning Claude Code
  or OpenCode as coding subagents with back-and-forth conversation.

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

- **Sub-agent isolation (Phase 1 complete)** — `SubAgentActor` provides
  ephemeral single-loop subagents with independent tool registries.
  Next: multi-turn sessions, disk-based definitions, `spawn_agent` tool.
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
- `docs/research/dynamic-context-discovery.md` — Tool/memory/skill
  discovery patterns (Anthropic defer_loading, Cursor file-based, DCL
  three-tier). Compressed index design, deferred memory retrieval
  decisions. Informs M2 (MCP) and M5 (memory) architecture.
