# Netclaw Implementation Plan

Last updated: 2026-04-01
Mode: build

This file is RALPH-consumable.

**Active milestone: Milestone 7 — Daemon Exposure and Hub Auth**
Three OpenSpec changes: `exposure-modes`, `hub-auth-framework`, `device-pairing`.

---

## Fix-it (Review after iter-15) — NOW

### Task R3.1: Add multi-scheme auth selector for DeviceBearer
**Source:** Review after iteration 15, finding #1
**Issue:** `DeviceTokenAuthenticationHandler` is registered but unreachable through the auth pipeline. `AddAuthentication(LoopbackAuthenticationHandler.SchemeName)` makes Loopback the only scheme tried by `[Authorize]`. Non-loopback connections with valid bearer tokens get 401 because DeviceBearer is never invoked. Need a `PolicyScheme` or `ForwardDefaultSelector` that delegates to DeviceBearer when an `Authorization: Bearer` header is present, otherwise falls back to Loopback.
**Done when:**
- [x] Auth pipeline uses a selector scheme (e.g., `PolicyScheme` with `ForwardDefaultSelector`) that tries DeviceBearer when `Authorization: Bearer` header is present, otherwise Loopback
- [x] Integration test: remote connection with valid bearer token authenticates successfully through `[Authorize]` on SessionHub
- [x] Integration test: loopback connection without bearer token still authenticates via Loopback scheme
**Verification:** L2

### Task R3.2: Add `.AllowAnonymous()` to exchange endpoint
**Source:** Review after iteration 15, finding #2
**Issue:** `POST /api/pair/exchange` is designed as unauthenticated but relies on implicit anonymity (no fallback authorization policy). Adding `options.FallbackPolicy` in the future would silently break pairing. Explicit `.AllowAnonymous()` documents intent and prevents breakage.
**Done when:**
- [x] `MapPost("/api/pair/exchange", ...)` chain includes `.AllowAnonymous()`
**Verification:** L1

### Task R3.3: Narrow DeviceRegistry.VerifyToken catch to FormatException
**Source:** Review after iteration 15, finding #3
**Issue:** `DeviceRegistry.VerifyToken()` at line 176 uses bare `catch` that swallows all exceptions. Only `FormatException` (from malformed base64url/hex) is expected. Broader exceptions (e.g., `CryptographicException`) should propagate.
**Done when:**
- [x] `catch` in `DeviceRegistry.VerifyToken` narrowed to `catch (FormatException)`
**Verification:** L1

### Task R3.4: Sync device-pairing/tasks.md section 10 checkboxes
**Source:** Review after iteration 15, finding #4
**Issue:** `openspec/changes/device-pairing/tasks.md` section 10 tasks 10.1 (DeviceRegistry tests), 10.2 (PairingCodeService tests), 10.3 (DeviceTokenAuthenticationHandler tests) are unchecked despite being implemented in iterations 14-15.
**Done when:**
- [x] Tasks 10.1, 10.2, 10.3 in `openspec/changes/device-pairing/tasks.md` marked `[x]`
**Verification:** L1

---

## Fix-it (Review after iter-05) — NOW

### Task R1.1: Sync OpenSpec tasks.md checkboxes for exposure-modes
**Source:** Review after iteration 5, finding #1
**Issue:** `openspec/changes/exposure-modes/tasks.md` only has tasks 1.1, 1.2, 1.4 checked. All tasks completed in M7.A2-A5 (1.3, 2.1, 2.2, 3.1-3.5, 4.1-4.5, 5.1-5.7, 7.1-7.5) remain unchecked despite being implemented and verified.
**Done when:**
- [x] All tasks in `openspec/changes/exposure-modes/tasks.md` that were implemented in iterations 1-5 have their checkboxes marked `[x]`
**Verification:** L1

### Task R1.2: Eliminate silent fallback in ExposureModeExtensions.ToWireValue()
**Source:** Review after iteration 5, finding #2
**Issue:** `ExposureModeExtensions.ToWireValue()` in `WizardConfigBuilder.cs:340-347` uses `_ => "local"` — a silent fallback that violates CLAUDE.md's "No silent fallbacks" rule. Also: `ExposureModeDoctorCheck.ToWireValue()` and `ExposureModeValidationService` inline switch each use different fallback strategies.
**Done when:**
- [x] `ExposureModeExtensions.ToWireValue()` in `WizardConfigBuilder.cs` throws on unknown enum values instead of defaulting to "local"
- [x] `ExposureModeDoctorCheck.ToWireValue()` throws on unknown enum values instead of using `ToString()`
- [x] `ExposureModeValidationService.StartAsync()` inline wire-value switch at line 60-66 throws on unknown enum values instead of using `ToString()`
**Verification:** L1

### Task R1.3: DaemonConfig single-bind refactor in Program.cs
**Source:** Review after iteration 5, finding #3
**Issue:** `DaemonConfig.BindFromConfiguration()` is called twice in `Program.cs` (line ~100 for WebHost URL, line ~324 for DI singleton), creating two separate instances. If config were modified between calls, WebHost bind address and DI singleton would silently diverge.
**Done when:**
- [x] `DaemonConfig` is computed once in `RunDaemonAsync`, used for `UseUrls`, and passed into `ConfigureDaemonServices` for DI registration (instead of re-parsing)
**Verification:** L1

---

## Review Fix-it: Sessions `--json` flag does not imply `--once`

**Source:** RALPH run 20260306-185029, review-after-iter-06 (finding F.1)
**Surface area:** `src/Netclaw.Cli/Program.cs`
**Verification:** L2

Done when:
- [x] `--json` case in the sessions argument parser also sets `onceMode = true` (line ~463 in Program.cs).
- [x] `netclaw sessions --json` produces JSON output and exits (does not fall through to TUI).
- [x] Smoke test updated to verify `sessions --json` exits 0 or 1 (not TUI launch).

Rules for loop execution:
- RALPH only picks tasks from the **active milestone** listed above.
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
- [x] Tests use Termina `VirtualTerminal` + `VirtualInputSource` for headless wizard testing.
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
- [x] `netclaw-identity` system skill provides triage guidance for identity file content.

> _Design note: conversational bootstrap was replaced with wizard-based bootstrap.
> The agent refines personality through conversation using `file_write` on identity
> files, guided by the `netclaw-identity` skill._

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
- [x] `netclaw-memory/1.1.0.md` — 4-tool surface, two-phase retrieval, update/delete, backend notes.
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

## Milestone 6: Stability and CLI Reliability (0.3.2)

**GitHub Milestone:** [0.3.2](https://github.com/Aaronontheweb/netclaw/milestone/1)

**Goal:** Fix post-0.3.1 regressions in search, session catalog, SignalR
resume, Slack error diagnostics, reminder execution, and CLI ergonomics.

### Task M6.1: Fix Brave Search gzip parse failure

**GitHub Issue:** #161
**Surface area:** `Netclaw.Search`
**Verification:** L2

Done when:
- [x] `BraveSearchBackend` correctly handles gzip-encoded responses (the `0x1F` byte is the gzip magic number; `ReadAsStringAsync` does not auto-decompress when `AcceptEncoding: gzip` is set manually).
- [x] Response `Content-Encoding` and `Content-Type` validated before JSON parse; unexpected encoding produces a controlled `SearchBackendResult.Error` with response metadata (status, content-type, content-encoding).
- [x] Tests with mock HTTP handler returning gzip-compressed JSON verify successful parse.
- [x] Test with mock returning unexpected binary content verifies controlled error path.

### Task M6.2: Harden session catalog schema migration

**GitHub Issue:** #162
**Surface area:** `Netclaw.Daemon` (`SessionCatalogService`)
**Verification:** L2

Done when:
- [x] `SessionCatalogService` auto-creates the `sessions` table using the `003_sessions_table.sql` schema when `DetectSchemaMode` returns `Missing`, instead of silently returning empty results.
- [x] Legacy schema (`session_id` column without `persistence_id`) is migrated to current schema via `ALTER TABLE` or table recreation on startup.
- [x] `ListRecent` returns explicit degraded status (empty list + warning log) only when migration itself fails, not on schema mismatch.
- [x] Tests verify: missing table → auto-create; legacy schema → auto-migrate; current schema → no-op.

### Task M6.3: Fix SignalR session stall after idle passivation

**GitHub Issue:** #163
**Surface area:** `Netclaw.Daemon` (`SessionRegistry`), `Netclaw.Cli` (`DaemonClient`)
**Verification:** L2

Done when:
- [x] `SessionRegistry.PublishOutput` logs when output is dropped due to missing connection binding (currently silently returns).
- [x] `SessionRegistry.AttachSessionAsync` re-materializes the output stream when attaching to a session whose Akka.Streams output has completed (post-passivation recovery creates new pipeline but old output sink is dead).
- [x] `DaemonClient` reconnect flow re-attaches to the active session after SignalR reconnection, not just re-establishes the SignalR connection.
- [x] Correlation logging added: log session attach/detach events with connection ID and session ID for post-mortem tracing.
- [x] Test: simulate disconnect → reconnect → send message → verify output delivery resumes.

### Task M6.4: Add correlation IDs and cause categories to Slack error fallback

**GitHub Issue:** #164
**Surface area:** `Netclaw.Channels.Slack` (`SlackThreadBindingActor`), `Netclaw.Actors` (`LlmSessionActor`)
**Verification:** L2

Done when:
- [x] `ErrorOutput` includes a `CorrelationId` (GUID) and `ErrorCategory` enum (`ToolFailure`, `ProviderFailure`, `StreamFailure`, `Timeout`, `Unknown`).
- [x] `SlackThreadBindingActor` includes correlation ID in the Slack fallback message (e.g., `:warning: Error processing your message (ref: abc123). Check logs for details.`).
- [x] Session log entries for errors include the same correlation ID for cross-referencing.
- [x] `LlmSessionActor` categorizes errors when emitting `ErrorOutput` (tool execution failure vs provider failure vs timeout).
- [x] Tests verify correlation ID propagation from error source through to output.

### Task M6.5: Add structured reminder execution diagnostics

**GitHub Issue:** #165
**Surface area:** `Netclaw.Actors.Reminders` (`ReminderExecutionActor`, `ReminderManagerActor`)
**Verification:** L2

Done when:
- [x] `ReminderExecutionActor` logs structured execution lifecycle: `{ReminderId, ExecutionId, Phase, DueAt, DispatchedAt, CompletedAt, Success, ErrorType, ErrorMessage}`.
- [x] All exceptions in reminder execution (including inner exceptions) are logged with full stack trace, not just `Message`.
- [x] `ReminderManagerActor.HandleReminderFiredAsync` logs the reminder ID, definition title, and schedule type when a reminder fires.
- [x] `netclaw status` includes reminder health counters: scheduled count, active executions, failed count (from `ReminderManagerActor` state, exposed via health endpoint).
- [x] Test: simulate reminder execution failure → verify structured log contains full exception chain.

### Task M6.6: Add single-shot CLI mode and CI smoke coverage

**GitHub Issue:** #166
**Surface area:** `Netclaw.Cli`
**Verification:** L2

Done when:
- [x] `netclaw sessions --once` lists sessions and exits (no TUI, plain text or JSON output, non-zero on failure).
- [x] `netclaw chat -p "..."` (headless mode, already exists) has deterministic exit code: 0 on success, non-zero on provider/session failure.
- [x] `netclaw status` already works as single-shot — verify exit codes are correct (0=healthy, 1=error, 2=degraded).
- [x] Smoke test script (`scripts/smoke/cli-smoke.sh`) exercises: `netclaw version`, `netclaw status`, `netclaw doctor`, `netclaw sessions --once` with expected exit codes.
- [x] CI workflow runs smoke script (can run without live daemon for offline commands; daemon-dependent commands skipped when daemon unavailable).

### Task M6.7: Make bare `netclaw` show usage error

**GitHub Issue:** #167
**Surface area:** `Netclaw.Cli` (`Program.cs`)
**Verification:** L2

Done when:
- [x] `netclaw` with no arguments prints help text and exits with code 2 (not 0, not 1).
- [x] Default `mode = "chat"` fallback removed; bare invocation no longer launches chat TUI.
- [x] Unknown commands print error message + help text and exit with code 2 (currently falls through to chat).
- [x] Tests verify: no-args → exit 2 + help output; unknown command → exit 2 + error.

### Task M6.8: Include update availability in `netclaw status`

**GitHub Issue:** #168
**Surface area:** `Netclaw.Cli` (`Program.cs` status output), `Netclaw.Daemon` (health endpoint)
**Verification:** L2

Done when:
- [x] `netclaw status` output includes current version, latest available version, and update state (`up-to-date` / `update-available` / `unknown`).
- [x] Update check is non-blocking with a short timeout (3s); failure results in `update status: unknown` with reason, not a command failure.
- [x] JSON output mode includes update info in the response payload.
- [x] Test: mock GitHub releases API → verify `update-available` when newer version exists; verify `unknown` on timeout/error.

## Milestone 7: Daemon Exposure and Hub Auth

**OpenSpec Changes:**
- `openspec/changes/exposure-modes/`
- `openspec/changes/hub-auth-framework/`
- `openspec/changes/device-pairing/`

**Goal:** Make the daemon safely reachable beyond loopback. Configurable bind
address, exposure mode declaration with tunnel validation, scheme-agnostic hub
authentication, and device pairing for self-hosted remote access.

**Dependency chain:** exposure-modes → hub-auth-framework → device-pairing.
Phases below follow this order.

### Phase A: Exposure Mode Configuration (exposure-modes change)

### Task M7.A1: ExposureMode enum and DaemonConfig type

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md` (SEC-005)
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/exposure-modes/`
**OpenSpec Tasks:** exposure-modes 1.1, 1.2, 1.4
**Surface area:** `Netclaw.Configuration`
**Verification:** L1

Done when:
- [x] `ExposureMode` enum exists with `Local`, `TailscaleServe`, `TailscaleFunnel`, `CloudflareTunnel` values and `JsonStringEnumConverter` support for kebab-case.
- [x] `DaemonConfig` record exists with `Host` (string, default `"127.0.0.1"`), `Port` (int, default `5199`), `ExposureMode` (default `Local`).
- [x] `DaemonConfig` is registered in daemon DI bound from `IConfiguration` section `"Daemon"`.
- [x] Unit tests verify deserialization from JSON with kebab-case enum values, defaults, and missing section.

### Task M7.A2: JSON schema and daemon bind address

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md` (SEC-005)
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/exposure-modes/`
**OpenSpec Tasks:** exposure-modes 1.3, 2.1, 2.2
**Surface area:** `Netclaw.Configuration`, `Netclaw.Daemon`
**Verification:** L2

Done when:
- [x] `Daemon` section added to `netclaw-config.v1.schema.json` with `Host` (string), `Port` (integer), `ExposureMode` (string enum), all with defaults. Section is optional.
- [x] `Program.cs` reads `DaemonConfig.Host` and `DaemonConfig.Port` instead of hardcoded `UseUrls("http://127.0.0.1:5199")`.
- [x] Existing `DaemonApi.ResolveEndpoint()` in CLI continues to work (reads `Daemon:Endpoint`, unaffected).
- [x] Schema validation test verifies valid `Daemon` section accepted, invalid enum rejected, missing section accepted.

### Task M7.A3: Startup prerequisite validation

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md` (SEC-005)
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/exposure-modes/`
**OpenSpec Tasks:** exposure-modes 3.1–3.5
**Surface area:** `Netclaw.Daemon`
**Verification:** L2

Done when:
- [x] `ExposureModeValidationService : IHostedService` reads `DaemonConfig` and validates tunnel prerequisites.
- [x] Tailscale modes check for `tailscaled` process; Cloudflare mode checks for `cloudflared` process.
- [x] On failure: logs descriptive error naming the missing prerequisite and throws to fail startup.
- [x] `Local` mode skips all tunnel validation.
- [x] Unit tests verify local skips, non-local with missing process throws.

### Task M7.A4: Doctor check for exposure health

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md` (SEC-005)
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/exposure-modes/`
**OpenSpec Tasks:** exposure-modes 4.1–4.5
**Surface area:** `Netclaw.Cli`
**Verification:** L2

Done when:
- [x] `ExposureModeDoctorCheck : IDoctorCheck` reads `DaemonConfig` from `netclaw.json`.
- [x] Reports warning when bind address is non-loopback and exposure mode is `local`.
- [x] Reports error when exposure mode is non-local and tunnel process not detected.
- [x] Reports pass when mode is `local` with loopback or non-local with healthy tunnel.
- [x] Registered in `DoctorRegistrationExtensions`.
- [x] Unit tests verify warning, error, and pass cases.

### Task M7.A5: Init wizard exposure mode step

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md` (Step 5)
**OpenSpec Capabilities:** `openspec/specs/netclaw-onboarding/spec.md`
**OpenSpec Changes:** `openspec/changes/exposure-modes/`
**OpenSpec Tasks:** exposure-modes 5.1–5.7
**Surface area:** `Netclaw.Cli`
**Verification:** L1

Done when:
- [x] `DaemonConfigSection` record added to `WizardConfigBuilder` typed sections.
- [x] `ExposureModeStepViewModel : IWizardStepViewModel` with `SelectionListNode` for four modes, `local` pre-selected.
- [x] `ExposureModeStepView` renders mode descriptions and risk indicators.
- [x] High-risk warning panel with explicit confirmation for `tailscale-funnel` and `cloudflare-tunnel`.
- [x] Informational notice for `tailscale-serve`.
- [x] `ContributeConfig` writes `Daemon` section only for non-default mode (local = omit).
- [x] Step inserted after security posture, before Slack in `InitWizardViewModel`.
- [x] Unit tests verify config contribution per mode.

### Task M7.A6: Hot-reload exclusion and spec updates

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/exposure-modes/`
**OpenSpec Tasks:** exposure-modes 6.1, 8.1, 8.2
**Surface area:** `Netclaw.Daemon`, `docs/spec/`
**Verification:** L1

Done when:
- [x] `ConfigWatcherService` / `RestartCoordinator` does not apply `Daemon` section changes during hot-reload; logs warning that restart is required.
- [x] `SPEC-006` updated to mark exposure mode configuration as implemented.
- [x] `SPEC-011` updated to reference `DaemonConfig` instead of hardcoded URL.

### Phase B: Hub Auth Framework (hub-auth-framework change)

### Task M7.B1: Claim types and ClaimsPrincipalMapper

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/hub-auth-framework/`
**OpenSpec Tasks:** hub-auth-framework 1.1–1.3
**Surface area:** `Netclaw.Configuration`, `Netclaw.Actors`
**Verification:** L1

Done when:
- [x] `NetclawClaimTypes` static class exists with `netclaw:principal`, `netclaw:transport`, `netclaw:device-id` constants.
- [x] `ConnectionIdentity` record exists with `PrincipalClassification`, `TransportAuthenticity`, `SenderId`.
- [x] `ClaimsPrincipalMapper` converts `ClaimsPrincipal` → `ConnectionIdentity`, falling back to `UntrustedExternal` / `Unknown`.
- [x] Unit tests cover loopback claims, bearer claims, and missing claims.

### Task M7.B2: Loopback authentication scheme

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/hub-auth-framework/`
**OpenSpec Tasks:** hub-auth-framework 2.1–2.4
**Surface area:** `Netclaw.Daemon`
**Verification:** L2

Done when:
- [x] `LoopbackAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>` checks `RemoteIpAddress` against `127.0.0.1` and `::1`.
- [x] Loopback match returns success with `Operator` + `LocalProcess` claims.
- [x] Non-loopback returns `AuthenticateResult.NoResult()`.
- [x] Registered as default authentication scheme in daemon DI.
- [x] Unit tests verify loopback → success, non-loopback → NoResult.

### Task M7.B3: Hub authorization and middleware

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/hub-auth-framework/`
**OpenSpec Tasks:** hub-auth-framework 3.1–3.3
**Surface area:** `Netclaw.Daemon`
**Verification:** L2

Done when:
- [x] `AddAuthentication()` and `AddAuthorization()` registered in daemon `Program.cs`.
- [x] `[Authorize]` attribute added to `SessionHub`.
- [x] `app.UseAuthentication()` and `app.UseAuthorization()` in middleware pipeline before hub mapping.
- [x] Integration test: unauthenticated non-loopback connection gets 401.
- [x] Integration test: loopback connection succeeds.

### Task M7.B4: Identity propagation into MessageSource

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/hub-auth-framework/`
**OpenSpec Tasks:** hub-auth-framework 4.1–4.4, 5.1–5.2
**Surface area:** `Netclaw.Daemon`, `Netclaw.Cli`
**Verification:** L2

Done when:
- [x] `SessionRegistry.CreateSessionAsync` and `SendMessageAsync` accept `ClaimsPrincipal` parameter.
- [x] `ClaimsPrincipalMapper` injected into `SessionRegistry`.
- [x] `MessageSource.Principal`, `Provenance.TransportAuthenticity`, and `SenderId` populated from `ConnectionIdentity`.
- [x] `SessionHub` passes `Context.User` to all `SessionRegistry` calls.
- [x] CLI `DaemonClient` works without changes for loopback (verified).
- [x] `ConfigureAccessToken` extension point on `HubConnectionBuilder` for future bearer token attachment.
- [x] Unit test verifies `MessageSource` populated from claims.

### Phase C: Device Pairing (device-pairing change)

### Task M7.C1: Device registry and bearer token scheme

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/device-pairing/`
**OpenSpec Tasks:** device-pairing 1.1–1.3, 2.1–2.6
**Surface area:** `Netclaw.Configuration`, `Netclaw.Daemon`
**Verification:** L2

Done when:
- [x] `PairedDevice` record with `Name`, `TokenHash`, `Salt`, `CreatedAt`, `LastUsedAt`.
- [x] `DeviceRegistry` service reads/writes `devices.json` — list, add, remove, lookup-by-hash, update last-used.
- [x] `IRemoteAuthSchemeRegistration` marker interface for startup validation.
- [x] `DeviceTokenAuthenticationHandler` reads `Authorization: Bearer` header, hashes with salt, validates against registry.
- [x] Valid token → `Operator` / `Verified` / device name; invalid → Fail; missing → NoResult.
- [x] Registered alongside loopback scheme in daemon DI.
- [x] Unit tests for registry CRUD and auth handler.

### Task M7.C2: Pairing code service and exchange endpoint

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/device-pairing/`
**OpenSpec Tasks:** device-pairing 3.1–3.4, 4.1–4.5
**Surface area:** `Netclaw.Daemon`
**Verification:** L2

Done when:
- [x] `PairingCodeService` generates, stores (in-memory), validates, and consumes codes.
- [x] Code format: 8 chars from `23456789ABCDEFGHJKLMNPQRSTUVWXYZ` as `XXXX-XXXX`, 5-min TTL, single-use.
- [x] Token generation: 32 bytes `RandomNumberGenerator`, base64url.
- [x] `POST /api/pair/exchange` endpoint — unauthenticated, accepts `{ code, deviceName }`, returns `{ token }`.
- [x] Rate limiting on exchange endpoint.
- [x] Unit tests for code lifecycle (generate, expire, consume, replace).

### Task M7.C3: CLI pairing commands

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/device-pairing/`
**OpenSpec Tasks:** device-pairing 5.1–5.5, 6.1–6.4
**Surface area:** `Netclaw.Cli`, `Netclaw.Daemon`
**Verification:** L2

Done when:
- [x] `netclaw daemon pair` connects via SignalR, invokes `GeneratePairingCode()`, displays code + expiry.
- [x] `GeneratePairingCode()` hub method requires `Operator` principal.
- [x] `netclaw daemon devices` lists paired devices (name, created, last-used).
- [x] `netclaw daemon devices revoke <name>` removes device.
- [x] Daemon logs pairing code to stdout for Docker container log access.
- [x] `netclaw pair <endpoint>` prompts for code + device name, POSTs to exchange endpoint.
- [x] On success: stores token in `secrets.json`, endpoint in `netclaw.json`.

### Cleanup (Postmortem 20260401-171023)

### Task CL.1: Rename or fix PairCommandConfigTests

**Source:** Postmortem adversarial review, finding CLEANUP-1
**Issue:** `src/Netclaw.Cli.Tests/Daemon/PairCommandConfigTests.cs` never calls `PairCommand.RunAsync()`. The test manually re-implements `PairCommand`'s config-write logic and verifies `ConfigFileHelper` round-trips correctly. The underlying test is useful (exercises secrets encryption round-trip), but the class name and XMLdoc falsely claim it tests `PairCommand`.
**Done when:**
- [x] Either: (a) rename test class to `ConfigFileHelperSecretsRoundTripTests` and update XMLdoc, or (b) extract config-write logic from `PairCommand` into a testable method and test it directly, or (c) update XMLdoc to remove the claim that it tests `PairCommand`
**Verification:** L1

---

### Task M7.C4: CLI token attachment and startup validation

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/device-pairing/`
**OpenSpec Tasks:** device-pairing 7.1–7.3, 8.1–8.2
**Surface area:** `Netclaw.Cli`, `Netclaw.Daemon`
**Verification:** L2

Done when:
- [x] `DaemonClient` detects non-loopback endpoint, reads `DeviceToken` from secrets, attaches via `AccessTokenProvider`.
- [x] On 401, CLI displays message suggesting `netclaw pair`.
- [x] `ExposureModeValidationService` extended: non-local mode fails startup if no paired devices and no `IRemoteAuthSchemeRegistration`.
- [x] Unit test: token attachment for non-loopback, skip for loopback.
- [x] Integration test: non-local + no devices → startup failure.

### Task M7.C5: Pairing smoke test in CI

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-gateway-security/spec.md`
**OpenSpec Changes:** `openspec/changes/device-pairing/`
**OpenSpec Tasks:** device-pairing 9.1–9.2
**Surface area:** `scripts/smoke/check.sh`
**Verification:** L2

Done when:
- [x] Pairing smoke test section in `scripts/smoke/check.sh` exercises full lifecycle: generate code → exchange → verify device list → connect with token → revoke → verify rejection.
- [x] Smoke test runs after existing session/stats tests, before teardown.

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
