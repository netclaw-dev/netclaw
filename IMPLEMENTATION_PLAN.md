# Netclaw Implementation Plan

Last updated: 2026-02-23
Mode: build

This file is RALPH-consumable.

Rules for loop execution:
- RALPH always picks the first task with unchecked `Done when` items.
- One iteration completes one task block.
- Task metadata must include PRD + OpenSpec references.
- Commits that complete a task must update this file and the referenced
  `openspec/changes/*/tasks.md` entries.

---

## Phase 0: Planning and Infrastructure Baseline (Completed)

### Task 0.1: Establish product planning baseline

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`, `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-session/spec.md`, `openspec/specs/netclaw-gateway-security/spec.md`, `openspec/specs/netclaw-acl/spec.md`, `openspec/specs/netclaw-slack-socket/spec.md`
**OpenSpec Changes:** `openspec/changes/define-netclaw-mvp-foundation/`
**Surface area:** planning
**Verification:** L0

Done when:
- [x] PRDs and engineering specs are created and cross-referenced.
- [x] OpenSpec capabilities exist for core MVP behavior.
- [x] `openspec validate --all --no-interactive` passes.

### Task 0.2: Replace template scaffold with Netclaw projects

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-session/spec.md`
**OpenSpec Changes:** `openspec/changes/define-netclaw-mvp-foundation/`
**Surface area:** runtime scaffold
**Verification:** L1

Done when:
- [x] `Netclaw.slnx` exists and template `SampleSln.slnx` is removed.
- [x] `src/Akka.Agents` and `src/Netclaw.App` exist on .NET 10.
- [x] `dotnet build Netclaw.slnx` passes.

### Task 0.3: Import RALPH loop infrastructure with OpenSpec terminology

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**OpenSpec Capabilities:** `openspec/specs/netclaw-cli/spec.md`, `openspec/specs/netclaw-testing/spec.md`
**OpenSpec Changes:** `openspec/changes/design-ops-console-and-cli-v1/`, `openspec/changes/add-provider-smoke-and-ci-independence/`
**Surface area:** tooling/process
**Verification:** L0

Done when:
- [x] `ralph.sh` and `ralph-opencode.sh` are present and executable.
- [x] `.claude/skills/ralph-loop.md`, `.claude/skills/ralph-run-diagnostics.md`, and `.claude/skills/ralph-output-adversarial-review.md` exist locally.
- [x] RALPH skills/scripts are updated to reference OpenSpec artifacts and validation.
- [x] `.gitignore` includes `.ralph/`, `.planning/`, `.code-health/`, and local Claude settings ignore.

---

## Phase 0.5: Vision Alignment and Spec Revision (Active)

Product vision was significantly expanded on 2026-02-21. Netclaw is no longer
just a Slack chat assistant — it is an always-on autonomous operations agent.
All planning artifacts must be revised to match before implementation begins.

See `PROJECT_CONTEXT.md` for the full revised vision.
See Memorizer memory "Netclaw — Full Product Vision (Interview Feb 2026)" for
interview context.

### Task 0.5.1: Research agent soul patterns from comparable projects

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md` (to be revised)
**OpenSpec Capabilities:** `openspec/specs/netclaw-session/spec.md`
**OpenSpec Changes:** n/a (research task)
**Surface area:** planning
**Verification:** L0

Research OpenClaw, IronClaw, ZeroClaw, and similar self-hosted LLM assistant
projects for patterns around:
- Agent soul / personality configuration (markdown-based system prompts)
- Memory.md and persistent memory conventions
- Tooling configuration and capability discovery
- Onboarding wizard flows
- Scheduled task management

Done when:
- [x] Research findings are saved to Memorizer with tag `netclaw-research`.
- [x] Summary document created at `docs/research/agent-patterns.md`.
- [x] Key patterns identified for adoption or rejection with rationale.

### Task 0.5.2: Revise PRDs to match expanded product vision

**PRD:** all `docs/prd/PRD-*.md`
**OpenSpec Capabilities:** all `openspec/specs/*/spec.md`
**OpenSpec Changes:** all active changes
**Surface area:** planning
**Verification:** L0

Current PRDs describe a narrower "chat assistant with ACL" scope. They need
revision to reflect the full vision:
- PRD-001: expand MVP scope (local memory, scheduling, tool access, self-config)
- PRD-002: keep security envelope, add capability self-discovery context
- PRD-003: defer ops console to Phase 5
- PRD-004: revise CLI to focus on onboarding wizard
- PRD-005: update provider strategy (OpenRouter primary, MSFT.EXT.AI pluggable)
- PRD-006: expand MCP role (Memorizer as external memory tier)
- New PRD needed: agent personality / soul and local memory system
- New PRD needed: scheduling and periodic task management
- New PRD needed: input adapters (ambient channels, webhooks, timers)

Done when:
- [x] All existing PRDs revised to align with `PROJECT_CONTEXT.md` vision.
- [x] New PRDs created for local memory, scheduling, and input adapters.
- [x] `docs/prd/README.md` index updated.

### Task 0.5.3: Revise engineering specs to match expanded PRDs

**PRD:** all revised PRDs from Task 0.5.2
**OpenSpec Capabilities:** all `openspec/specs/*/spec.md`
**OpenSpec Changes:** all active changes
**Surface area:** planning
**Verification:** L0

Engineering specs need revision to include:
- Concrete message protocol types (from Memorizer design memories)
- Local memory file format and loading mechanism
- Scheduling actor design
- Configuration file format and self-modification
- Environment capability discovery mechanism
- Serialization strategy (protobuf-net with field numbers)

Done when:
- [x] Engineering specs revised to include concrete type definitions.
- [x] Protocol message types documented with code examples.
- [x] Local memory and config file format specified.
- [x] Scheduling mechanism specified.

### Task 0.5.4: Revise OpenSpec capabilities and create new change plans

**PRD:** revised PRDs from Task 0.5.2
**OpenSpec Capabilities:** all `openspec/specs/*/spec.md`
**OpenSpec Changes:** to be created
**Surface area:** planning
**Verification:** L0

OpenSpec artifacts need to match the revised PRDs:
- Update existing capability specs for expanded scope
- Create new capability specs for local memory, scheduling, input adapters
- Create new change plans aligned with the revised phasing
- Archive or revise existing change plans that no longer match

Done when:
- [x] OpenSpec capabilities updated to match revised PRDs.
- [x] New capability specs created for new subsystems.
- [x] Change plans created for revised Phase 1 (Chat + Memory MVP).
- [x] `openspec validate --all --no-interactive` passes.

### Task 0.5.5: Revise implementation plan Phase 1+ for new phasing

**PRD:** revised PRDs
**OpenSpec Capabilities:** revised specs
**OpenSpec Changes:** revised changes
**Surface area:** planning
**Verification:** L0

Rewrite Phase 1 and subsequent phases to match the revised product vision
phasing from `PROJECT_CONTEXT.md`. Current Phase 1-5 are based on the old
narrower scope. New phasing:

1. Chat + Memory MVP
2. Input Expansion (ambient channels, webhooks, channel instructions)
3. Delegated Coding (Claude Code / OpenCode spawning)
4. Browser + Research (web automation, research pipelines)
5. Ops Console (web UI)

Done when:
- [x] Phase 1+ rewritten with concrete tasks, PRD refs, and OpenSpec refs.
- [x] Each task has clear done-when criteria suitable for RALPH execution.
- [x] Implementation plan is RALPH-consumable for Phase 1.

---

## Phase 1: Chat + Memory MVP

OpenSpec Changes:
- `openspec/changes/expand-mvp-for-autonomous-agent-vision/` (Tasks 1.1–1.6, 1.9–1.18)
- `openspec/changes/add-tui-adapter-and-config-hot-reload/` (Tasks 1.11–1.12, 1.19–1.25)

Full task breakdowns:
- `openspec/changes/expand-mvp-for-autonomous-agent-vision/tasks.md`
- `openspec/changes/add-tui-adapter-and-config-hot-reload/tasks.md`

> **Renumbering note (2026-02-21):** Tasks were reordered for TUI-first
> validation. The goal is to get a working "type → LLM responds" loop as fast
> as possible using Ollama, proving the actor system works end-to-end with a
> real model, then layer on capabilities incrementally. Completed task IDs
> (1.1–1.4, 1.6) are stable. Remaining tasks were renumbered into priority
> tiers. See git history for the previous ordering.

> **Gateway note:** Netclaw.App was temporarily changed from `Microsoft.NET.Sdk.Web`
> to `Microsoft.NET.Sdk` (plain console host) for the proof-of-concept console
> adapter. Task 1.11 restores `Microsoft.NET.Sdk.Web` for daemon modes per
> SPEC-011. Single-process architecture validated by research in
> `docs/research/agent-gateway-architecture.md`.

### Task 1.1: Framework protocol and persistence-safe message envelopes (DONE)

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** `openspec/specs/netclaw-session/spec.md`, `openspec/specs/netclaw-input-adapters/spec.md`
**Surface area:** actor framework
**Verification:** L2

Done when:
- [x] `SendUserMessage`, `TurnRecorded`, `SessionCompacted`, `TurnBroadcast`, `CompactionBroadcast` implemented with protobuf-net serialization.
- [x] `SerializableChatMessage` framework-owned type implemented (no direct persistence of MEAI types).
- [x] `SessionMessageExtractor` supports entity key patterns: `{channelId}/{threadTs}` and `schedule/{taskId}/{runTs}`.
- [x] Source metadata (adapter type, sender identity, channel, timestamp) on all commands.
- [x] Integration tests verify serialization round-trip and entity key extraction.

### Task 1.2: Session actor turn loop, persistence, and context management (DONE)

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** `openspec/specs/netclaw-session/spec.md`
**Research:** `docs/research/context-management-patterns.md`
**Surface area:** actor runtime
**Verification:** L2

Design decisions informed by cross-SDK research (OpenAI, LangChain, Semantic
Kernel, Anthropic, Google ADK, LlamaIndex — see research doc for sources).

Done when:
- [x] `LlmSessionActor` as `ReceivePersistentActor` with `SessionState` (immutable, decoupled from actor).
- [x] Turn loop: receive `SendUserMessage`, invoke `IChatClient`, persist `TurnRecorded`, emit typed outputs to subscribers.
- [x] Ready/Processing behavior states with message buffering during LLM calls.
- [x] Subscriber model with `OutputFilter` bitmask (Text, Thinking, ToolCalls, Usage).
- [x] Snapshot strategy per `SessionConfig.SnapshotInterval`.
- [x] Recovery from journal and snapshots. Kill-and-restore integration test.
- [x] `UsageOutput` enriched with context window metadata (`ContextWindowTokens`, `UsagePercent`).
- [x] `ChatMessageConverter` boundary conversion with round-trip tests.
- [x] `Compacting` behavior state: tiered approach per research findings.
- [x] Phase 1 of compaction: clear old tool results (replace with placeholder, keep N recent).
- [x] Phase 2 of compaction: structured summarization with domain-specific sections.
- [x] Structured compaction prompt template (task overview, current state, decisions, pending actions).
- [x] Pre-compaction memory flush: silent agentic turn extracts durable memories before context reset.
- [x] Optional `CompactionModelId` in `SessionConfig` for cheaper compaction model.
- [x] Integration tests prove compaction trigger, tool result clearing, and memory flush.

### Task 1.3: Session parent and entity routing (PARTIAL)

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** `openspec/specs/netclaw-session/spec.md`
**Surface area:** actor runtime
**Verification:** L2

Done when:
- [x] `GenericChildPerEntityParent` routes `IWithSessionId` messages to per-session children.
- [x] `SessionMessageExtractor` as `HashCodeMessageExtractor`.
- [x] `NetclawAkkaHostingExtensions.WithSessionManager()` wiring.
- [ ] Multi-key-pattern support (Slack and timer patterns) — deferred to Task 1.14.
- [ ] Tests verify entity lifecycle and message routing — deferred to Task 1.14.

### Task 1.4: Layered system prompt and personality (DONE)

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** agent personality
**Verification:** L2

Done when:
- [x] `~/.netclaw/` directory structure created on startup (soul/, projects/, environment/, schedules/, config/).
- [x] System prompt assembled from layers: PERSONALITY.md → INSTRUCTIONS.md → USER.md → project AGENTS.md → session context.
- [x] Missing layers handled gracefully.
- [x] Tests for prompt assembly with missing layers and project overlay injection.

### Task 1.6: Tool framework and MEAI registration (DONE)

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`, `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-tools/spec.md`, `openspec/specs/netclaw-model-providers/spec.md`
**Surface area:** tool framework
**Verification:** L2

Done when:
- [x] Tool registry registers `AIFunction` definitions through `Microsoft.Extensions.AI`.
- [x] Policy-filtered tool loading: session receives only tools matching ACL grants.
- [x] Tool invocation audit logging (tool name, session ID, timestamp, allow/deny).
- [x] Tool context added to session state at initialization.
- [x] Tests for registration, policy filtering, and audit logging.

---

### Tier 1: "Hello World" — prove the actor system works

### Task 1.7: OllamaSharp provider wiring (DONE)

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`
**OpenSpec:** `openspec/specs/netclaw-model-providers/spec.md`
**Surface area:** provider integration
**Verification:** L1
**Previously:** Task 1.8 (simplified — no fallback, no multi-provider)

Wire a real `IChatClient` to Ollama. No fallback, no multi-provider — just one
working connection.

Done when:
- [x] `OllamaSharp` added to `Directory.Packages.props` and `Netclaw.App.csproj`.
- [x] `OllamaApiClient` registered as `IChatClient` in DI.
- [x] `SessionConfig` populated with model defaults.
- [x] Model ID configurable via `appsettings.json` (`Ollama:Url`, `Ollama:Model`).

### Task 1.8: Bare console chat loop (DONE)

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**OpenSpec:** `openspec/specs/netclaw-input-adapters/spec.md`, `openspec/specs/netclaw-cli/spec.md`
**Surface area:** console adapter
**Verification:** L3
**Previously:** Tasks 1.13 + 1.14 (simplified — no Cocona, no Termina, no TUI)

Minimal console loop — no Cocona, no Termina, no TUI framework. Just prove the
actor system works end-to-end with a real LLM.

Done when:
- [x] `Program.cs` rewritten from `WebApplication` to `IHostBuilder` with Akka.Hosting.
- [x] Wired: `AddAkka` → `WithInMemoryJournal` → `WithInMemorySnapshotStore` → `WithNetclawActors`.
- [x] `IChatClient` (Ollama), `SessionConfig`, `ISystemPromptProvider` (static) registered.
- [x] `ConsoleAdapter` hosted service: generates session ID, sends `JoinSession`, `ReadLine` loop.
- [x] `ConsoleSubscriberActor` receives session outputs, writes to console.
- [x] `dotnet build Netclaw.slnx` passes, all existing tests pass.

---

### Tier 2: Make it useful

### Task 1.9: First-party tools (search, fetch, shell, GitHub)

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-tools/spec.md`
**Surface area:** tools
**Verification:** L2
**Previously:** Task 1.7

Done when:
- [x] Shell execution tool with timeout, output truncation, stdin closure, working directory.
- [x] File read and file write tools with path validation and output truncation.
- [x] Source-generated tool schemas via Roslyn incremental generator (ADR-001).
- [ ] ~~Web search tool~~ — deferred, not needed for minimal viable concept.
- [ ] ~~Web fetch tool~~ — deferred, not needed for minimal viable concept.

> **Note:** GitHub CLI access is handled via `shell_execute` + `gh` — no dedicated tool needed.
> Web search and web fetch deferred — shell + file tools are sufficient to prove the concept.

### Task 1.10: Multi-provider config system (DONE)

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`
**Surface area:** provider integration
**Verification:** L2
**Previously:** Task 1.8 (remainder — multi-provider, fallback)

Done when:
- [x] `Netclaw.Configuration` library with `ChatClientFactory`, `ProviderEntry`, `ModelSelection`.
- [x] `NetclawChatClientProvider` resolves clients by model role (main, compaction).
- [x] Layered config chain: netclaw.json + secrets.json + NETCLAW_* env vars.
- [x] Multi-provider support (Ollama, OpenRouter via OpenAI adapter).
- [ ] Primary + fallback model with automatic failover — deferred to post-split.

### Task 1.11: Daemon architecture scaffold (DONE)

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**Spec:** `docs/spec/SPEC-011-daemon-architecture.md`
**Surface area:** CLI framework + gateway
**Verification:** L1

Done when:
- [x] Termina and System.Reactive package references added to `Directory.Packages.props`.
- [x] `Netclaw.App.csproj` SDK changed to `Microsoft.NET.Sdk.Web`, Termina added.
- [x] `Program.cs` with mode routing: `run`/`chat`/headless use `WebApplication`, `init`/`doctor` use `Host`.
- [x] Shared config services extracted to `ConfigureConfigServices()` method.
- [x] Daemon-only services extracted to `ConfigureDaemonServices()` method.
- [x] SignalR hub stub mapped at `/hub/session`.
- [x] Health probe mapped at `/api/health/ready`.
- [x] `ConsoleChannel.cs` deleted (replaced by Termina TUI).
- [x] Crash logging to `~/.netclaw/logs/crash-{timestamp}.log`.

### Task 1.12: TUI chat adapter — in-process prototype (DONE)

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md` (CLI-011)
**Surface area:** TUI + adapter
**Verification:** L3

In-process prototype using `SessionPipeline` directly. Will be refactored to
thin SignalR client in Task 1.26.

Done when:
- [x] `ChatPage.cs` with `StreamingTextNode`, `TextInputNode`, paste debounce, scrolling.
- [x] `ChatViewModel.cs` uses `SessionPipeline` directly via DI.
- [x] Status bar with model name, token usage, context percentage.
- [x] Tool call spinners with elapsed time (`ElapsedTimeSegment`).
- [x] E2E: user types → session actor → LLM → streaming response in TUI.

---

### Tier 3: Daemon + Thin Client Split

> **Architecture revision (2026-02-23):** Netclaw follows the OpenClaw pattern
> — persistent daemon + thin CLI/TUI clients. Two binaries: `Netclaw.Daemon`
> (always-on service) and `Netclaw.Cli` (lightweight client connecting via
> SignalR). See SPEC-011 for full specification.

### Task 1.26: Project split — Netclaw.Daemon and Netclaw.Cli

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md` (Daemon Architecture)
**Spec:** `docs/spec/SPEC-011-daemon-architecture.md`
**Surface area:** project structure
**Verification:** L1

Split `Netclaw.App` into two projects with distinct dependency profiles.

Done when:
- [ ] `src/Netclaw.Daemon/` project created (`Microsoft.NET.Sdk.Web`).
- [ ] `src/Netclaw.Cli/` project created (`Microsoft.NET.Sdk`).
- [ ] Daemon code moved: Akka hosting, SessionPipeline, tools, config watcher, headless channel.
- [ ] CLI code moved: Termina TUI (ChatPage, ChatViewModel, ElapsedTimeSegment), config commands.
- [ ] Shared types remain in `Netclaw.Actors` (protocol) and `Netclaw.Configuration`.
- [ ] `Netclaw.Cli` references `Microsoft.AspNetCore.SignalR.Client`.
- [ ] `Netclaw.Daemon` references `Microsoft.AspNetCore.SignalR` (server).
- [ ] `Netclaw.slnx` updated. `dotnet build` passes.
- [ ] Old `Netclaw.App` removed.

### Task 1.27: Functional SessionHub in daemon

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md` (CLI-013)
**Spec:** `docs/spec/SPEC-011-daemon-architecture.md`
**Surface area:** gateway
**Verification:** L2

Make the SignalR hub functional — the primary API for all clients.

Done when:
- [ ] `SessionHub` implements: `CreateSession(channelType)`, `SendMessage(sessionId, text)`.
- [ ] `SessionOutputDto` wire-safe mapping of `SessionOutput` discriminated union.
- [ ] Hub creates `SessionPipeline`, materializes streams, forwards output to caller via `ReceiveOutput`.
- [ ] Connection lifecycle: sessions survive client disconnect/reconnect.
- [ ] Integration test: hub creates session, sends message, receives output.

### Task 1.28: SignalR client adapter in CLI

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md` (CLI-011)
**Spec:** `docs/spec/SPEC-011-daemon-architecture.md`
**Surface area:** CLI + TUI
**Verification:** L2

Replace `ChatViewModel`'s direct `SessionPipeline` usage with a SignalR client.

Done when:
- [ ] `DaemonClient` class wrapping `HubConnection` — exposes `IObservable<SessionOutput>` and `SendAsync(ChannelInput)`.
- [ ] `ChatViewModel` constructor takes `DaemonClient` instead of `SessionPipeline`.
- [ ] `ChatPage` unchanged — same rendering, same paste debounce, same status bar.
- [ ] Connection error handling: retry with backoff, clear error message on failure.
- [ ] E2E: `netclaw chat` → SignalR → daemon → LLM → streaming response in TUI.

### Task 1.29: Daemon management commands

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md` (CLI-012)
**Spec:** `docs/spec/SPEC-011-daemon-architecture.md`
**Surface area:** CLI
**Verification:** L1

Done when:
- [ ] `netclaw daemon start` — spawns `netclawd` as detached background process, writes PID to `~/.netclaw/netclaw.pid`.
- [ ] `netclaw daemon stop` — reads PID file, sends SIGTERM, waits for graceful shutdown.
- [ ] `netclaw daemon status` — reports running/stopped, PID, uptime.
- [ ] `netclaw daemon install` — creates systemd user service at `~/.config/systemd/user/netclaw.service`, enables linger.
- [ ] `netclaw daemon uninstall` — stops service, removes unit file.
- [ ] Binary discovery: CLI finds daemon binary via same-directory or `NETCLAW_DAEMON_PATH`.

### Task 1.30: Daemon-required CLI commands (query via SignalR)

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**Spec:** `docs/spec/SPEC-011-daemon-architecture.md`
**Surface area:** CLI
**Verification:** L1

CLI commands that query daemon state over SignalR. Each command connects,
sends one RPC, prints result, disconnects.

Done when:
- [ ] `netclaw session list|inspect|compact` — query session state from daemon.
- [ ] `netclaw tools list|policy` — query tool registry from daemon.
- [ ] `netclaw mcp list|validate|test` — query MCP connections from daemon.
- [ ] `netclaw schedule list|show|pause|resume|delete` — manage scheduled tasks via daemon.
- [ ] `netclaw memory show` — query agent memory from daemon.
- [ ] `netclaw acl validate|test|explain` — test against running policy engine.
- [ ] `netclaw test smoke` — end-to-end smoke test through daemon.
- [ ] All commands print clear error if daemon not running.

### Task 1.31: Offline CLI commands (local file I/O)

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**Surface area:** CLI
**Verification:** L1

CLI commands that work without the daemon by reading local files.

Done when:
- [ ] `netclaw doctor` — validate config files, probe daemon reachability, test provider connectivity.
- [ ] `netclaw config show|validate` — dump/validate merged config.
- [ ] `netclaw project list|add|remove` — manage project registry (local JSON files).
- [ ] `netclaw environment scan|show` — discover local system capabilities.
- [ ] `netclaw personality reset` — reset soul file to default.

---

### Task 1.13: ACL and policy engine with tool grants

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec:** `openspec/specs/netclaw-acl/spec.md`, `openspec/specs/netclaw-gateway-security/spec.md`
**Surface area:** security
**Verification:** L2
**Previously:** Task 1.5

Not needed for single-user local testing, but required before Slack adapter or
any multi-user scenario.

Done when:
- [ ] ACL parser supports channel rules, sender allowlists, mention/ambient mode, and tool grant categories (shell, web_search, web_fetch, github, mcp:{server}, config_write, schedule_write).
- [ ] Default deny enforced when no explicit allow.
- [ ] Self-configuration prohibition: agent cannot modify ACL/security through conversation.
- [ ] Invalid ACL blocks startup with actionable diagnostics.
- [ ] Shell execution boundaries enforced (SEC-009): timeout, output truncation, no stdin, working dir.
- [ ] Tool invocation audit logging.
- [ ] Policy decision tests cover all grant categories.

### Task 1.14: Multi-key routing (remaining Task 1.3 items)

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** `openspec/specs/netclaw-session/spec.md`
**Surface area:** actor runtime
**Verification:** L2
**Previously:** Remainder of Task 1.3

Multi-key-pattern support and routing tests. Only matters when multiple adapters
(TUI + Slack + scheduled tasks) coexist.

Done when:
- [ ] Multi-key-pattern support (Slack and timer patterns).
- [ ] Tests verify entity lifecycle and message routing.

---

### Tier 5: Full capability

### Task 1.15: MCP integration and Memorizer

**PRD:** `docs/prd/PRD-006-mcp-tool-integration.md`
**OpenSpec:** `openspec/specs/netclaw-mcp/spec.md`
**Surface area:** integration
**Verification:** L2
**Previously:** Task 1.9

Done when:
- [ ] MCP server profiles (named, stdio/SSE transport, enable/disable).
- [ ] Tool discovery at startup: connect, list tools, register as MEAI definitions.
- [ ] Graceful degradation: unavailable server returns error, agent continues, reconnect on next call.
- [ ] Memorizer store/search/get cycle works through session.
- [ ] Tests for connection, discovery, policy gating, degradation.

### Task 1.16: Local memory (project registry, environment inventory)

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** agent memory
**Verification:** L2
**Previously:** Task 1.10

Done when:
- [ ] Project registry (`projects/registry.json`): add, remove, list, validate paths, load at startup.
- [ ] Environment inventory (`environment/inventory.json`): scan for git, gh, claude, opencode, dotnet, node.
- [ ] Capability self-discovery at startup and on-demand rescan.
- [ ] Tests for project registry CRUD and environment scan.

### Task 1.17: Self-configuration through conversation

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** agent config
**Verification:** L2
**Previously:** Task 1.11

Done when:
- [ ] Agent modifies personality, instructions, user preferences, project registry, and environment through conversation.
- [ ] Validation-before-write and atomic file writes (temp + rename).
- [ ] Prohibited modification enforcement: reject ACL, security, tool grants, exposure, credentials.
- [ ] Tests for allowed modifications, prohibited modifications, validation failures.

### Task 1.18: Scheduling system

**PRD:** `docs/prd/PRD-008-scheduling-and-periodic-tasks.md`
**OpenSpec:** `openspec/specs/netclaw-scheduling/spec.md`
**Surface area:** scheduling
**Verification:** L2
**Previously:** Task 1.12

Done when:
- [ ] `ScheduleManagerActor` loads tasks from `schedules/tasks.json`, manages Akka timers.
- [ ] Chat-driven creation: interval and cron types, validate tool grants, persist task.
- [ ] Isolated execution: timer dispatches `SendUserMessage` with `schedule/{taskId}/{runTs}` entity key.
- [ ] Result reporting: post to configured Slack channel, silent-unless-notable mode.
- [ ] Guardrails: max concurrent (3), timeout (5min), consecutive failure auto-pause (5).
- [ ] Task management: list, pause, resume, delete via conversation and CLI.
- [ ] Tests for persistence, timer lifecycle, isolated execution, failure handling.

---

### Tier 6: Polish and ship

### Task 1.19: Config hot-reload

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md` (FR-016)
**OpenSpec:** `openspec/specs/netclaw-config-hot-reload/spec.md`, `openspec/specs/netclaw-session/spec.md`
**OpenSpec Changes:** `openspec/changes/add-tui-adapter-and-config-hot-reload/` (Section 5)
**Surface area:** runtime config
**Verification:** L2
**Previously:** Task 1.17

Done when:
- [ ] `ConfigWatcherService` as `IHostedService` with `FileSystemWatcher` per watched file.
- [ ] 500ms debounce for file change events.
- [ ] Validate-before-apply with rejection logging.
- [ ] Config file deletion handling (warn, keep existing config).
- [ ] ACL change events published to policy engine via Akka pub/sub.
- [ ] Provider change events published to provider factory via Akka pub/sub.
- [ ] MCP profile change events published to MCP manager via Akka pub/sub.
- [ ] Schedule change events published to `ScheduleManagerActor` via Akka pub/sub.
- [ ] Integration test: config file write → debounce → validate → actor notification.

### Task 1.20: Conversational personality bootstrap

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`, `openspec/specs/netclaw-onboarding/spec.md`
**OpenSpec Changes:** `openspec/changes/add-tui-adapter-and-config-hot-reload/` (Section 6)
**Surface area:** onboarding
**Verification:** L2
**Previously:** Task 1.18

Done when:
- [ ] First-run detection: trigger bootstrap when soul files don't exist on first `netclaw chat`.
- [ ] Bootstrap conversation: introduce, learn preferences, scan environment, write soul files, confirm.
- [ ] PERSONALITY.md, INSTRUCTIONS.md, USER.md written to config directory.
- [ ] Test: bootstrap triggers when files missing, skips when files exist.

### Task 1.21: Plain CLI commands

Superseded by Tasks 1.30 (daemon-required) and 1.31 (offline). See Tier 3.

### Task 1.22: TUI onboarding wizard (`netclaw init`)

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md` (CLI-010)
**OpenSpec:** `openspec/specs/netclaw-onboarding/spec.md`, `openspec/specs/netclaw-cli/spec.md`
**OpenSpec Changes:** `openspec/changes/add-tui-adapter-and-config-hot-reload/` (Section 3)
**Surface area:** TUI + onboarding
**Verification:** L3
**Wireframe:** `docs/ui/TUI-001-command-wireframes.md` (netclaw init)
**Previously:** Task 1.15

Done when:
- [ ] `InitCommand.cs` launches Termina wizard.
- [ ] `InitWizardPage.cs` with 7-step wizard layout (`PanelNode`, progress bar).
- [ ] `InitWizardViewModel.cs` with step state machine and back-navigation.
- [ ] Steps: LLM provider, Slack config, PostgreSQL, ACL bootstrap, MCP servers, exposure mode, health check.
- [ ] Config file written to `~/.netclaw/config/netclaw.json` on completion.

### Task 1.23: Slack Socket Mode adapter

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** `openspec/specs/netclaw-slack-socket/spec.md`, `openspec/specs/netclaw-input-adapters/spec.md`
**Surface area:** integration
**Verification:** L2
**Previously:** Task 1.20

Done when:
- [ ] Socket Mode connection and event handling (`app_mention`, `message`).
- [ ] Entity key extraction: `{channelId}/{threadTs}`.
- [ ] Reply delivery: subscribe to session broadcasts, post replies to originating thread.
- [ ] Reconnection on disconnect.
- [ ] End-to-end test proves message → reply loop.

### Task 1.24: E2E validation

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** all Phase 1 specs
**Surface area:** end-to-end
**Verification:** L4
**Previously:** Tasks 1.19 + 1.21

Done when:
- [ ] E2E: `netclaw chat` → session → tool call → streaming response.
- [ ] E2E: Slack message → session → tool call → reply in thread.
- [ ] E2E: scheduled task fires → fresh session → result posted to Slack.
- [ ] E2E: restart recovery preserves session context and scheduled tasks.
- [ ] E2E: config change → hot-reload → policy refresh verified.
- [ ] CI test suite passes without live provider credentials.
- [ ] Deploy to pi1 and verify Slack interaction.

### Task 1.25: Spec sync and archive

**OpenSpec Changes:** `openspec/changes/expand-mvp-for-autonomous-agent-vision/`, `openspec/changes/add-tui-adapter-and-config-hot-reload/`
**Surface area:** process
**Verification:** L0
**Previously:** Task 1.22

Done when:
- [ ] Delta specs synced to main specs for both changes.
- [ ] `openspec validate --all --no-interactive` passes.
- [ ] Changes archived.

---

## Phase 2: Input Expansion (Post-MVP)

Ambient channels, webhooks, channel instructions, onboarding wizard.
Tasks to be defined when Phase 1 is complete.

---

## Phase 3: Delegated Coding (Post-MVP)

Claude Code / OpenCode spawning, process monitoring.
Tasks to be defined when Phase 2 is complete.

---

## Phase 4: Browser + Research (Post-MVP)

Web automation, price monitoring, research pipelines.
Tasks to be defined when Phase 3 is complete.

---

## Phase 5: Ops Console (Post-MVP)

Web UI for config, sessions, diagnostics (PRD-003).
Tasks to be defined when Phase 4 is complete.

---

## Future Considerations

Patterns identified during implementation research that are deferred from
current phases but should inform future design decisions. Full analysis in
the linked research documents.

### Near-Term (incorporate during Phase 1)

- ~~**Max tool iterations circuit breaker**~~ — **DONE.** `MaxToolIterationsPerTurn`
  in `SessionConfig` (default 10). Forces text-only LLM call when limit reached.
  See: `docs/research/actor-llm-optimization-patterns.md` §2
- ~~**Parallel tool execution**~~ — **DONE.** `Task.WhenAll` for independent
  tool calls in `ExecuteToolsAsync`.
  See: `docs/research/actor-llm-optimization-patterns.md` §3
- **Retry with exponential backoff** — `IChatClient` decorator or actor-level
  retry for transient LLM errors. Critical for scheduled task reliability.
  See: `docs/research/actor-llm-optimization-patterns.md` §5

### Medium-Term (Phase 1 provider abstraction + TUI)

- **IChatClient decorator pipeline** — `CachingChatClient → RetryingChatClient
  → RateLimitingChatClient → ProviderChatClient`. Transparent to actor code.
  Natural fit for Task 1.10 (full provider abstraction).
  See: `docs/research/actor-llm-optimization-patterns.md` §1 (Tier 3)
- **Streaming responses** — Actor-level vs adapter-level streaming for TUI
  and Slack UX. Design decision needed before Task 1.12 (TUI adapter).
  See: `docs/research/actor-llm-optimization-patterns.md` §4

### Long-Term (Phase 2+)

- **Prompt cache warming** — Shared system prompt cache warmer actor. Low
  cost, benefits all sessions. Requires provider abstraction.
  See: `docs/research/actor-llm-optimization-patterns.md` §1 (Tier 1)
- **Cache-aware compaction** — Anthropic cache control breakpoints on
  system prompt and compaction summary boundaries.
  See: `docs/research/actor-llm-optimization-patterns.md` §1 (Tier 2)
- **Sub-agent isolation** — Child task actors with independent context
  windows. Architecture already supports it (`SessionState` is decoupled).
  Natural entry point at Phase 3 (Delegated Coding).
  See: `docs/research/actor-llm-optimization-patterns.md` §6

### Research Documents

- `docs/research/context-management-patterns.md` — Cross-SDK compaction
  and memory patterns (OpenAI, LangChain, Semantic Kernel, Anthropic,
  Google ADK, LlamaIndex, CrewAI, Haystack)
- `docs/research/agent-patterns.md` — Agent soul, personality, tooling,
  and onboarding patterns from comparable projects
- `docs/research/actor-llm-optimization-patterns.md` — Prompt caching,
  safety circuit breakers, parallel execution, streaming, retry, and
  sub-agent isolation patterns for actor-based LLM systems
- `docs/research/agent-gateway-architecture.md` — Architecture analysis
  (OpenClaw, IronClaw, PicoClaw). Informed the daemon + thin client split.
