# Netclaw Implementation Plan

Last updated: 2026-02-21
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
- `openspec/changes/expand-mvp-for-autonomous-agent-vision/` (Tasks 1.1–1.12)
- `openspec/changes/add-tui-adapter-and-config-hot-reload/` (Tasks 1.13–1.19, 1.22)

Full task breakdowns:
- `openspec/changes/expand-mvp-for-autonomous-agent-vision/tasks.md`
- `openspec/changes/add-tui-adapter-and-config-hot-reload/tasks.md`

### Task 1.1: Framework protocol and persistence-safe message envelopes

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** `openspec/specs/netclaw-session/spec.md`, `openspec/specs/netclaw-input-adapters/spec.md`
**Surface area:** actor framework
**Verification:** L2

Done when:
- [ ] `SendUserMessage`, `TurnRecorded`, `SessionCompacted`, `TurnBroadcast`, `CompactionBroadcast` implemented with protobuf-net serialization.
- [ ] `SerializableChatMessage` framework-owned type implemented (no direct persistence of MEAI types).
- [ ] `SessionMessageExtractor` supports entity key patterns: `{channelId}/{threadTs}` and `schedule/{taskId}/{runTs}`.
- [ ] Source metadata (adapter type, sender identity, channel, timestamp) on all commands.
- [ ] Integration tests verify serialization round-trip and entity key extraction.

### Task 1.2: Session actor core with persistence and turn loop

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** `openspec/specs/netclaw-session/spec.md`
**Surface area:** actor runtime
**Verification:** L2

Done when:
- [ ] `LlmSessionActor` recovers state from PostgreSQL journal/snapshots.
- [ ] Turn loop: receive `SendUserMessage`, invoke `IChatClient`, persist `TurnRecorded`, emit `TurnBroadcast` via pub/sub.
- [ ] Snapshot strategy and compaction via `SummarizingChatReducer`.
- [ ] Pre-compaction memory flush: silent agentic turn saves durable memories before context resets.
- [ ] Integration tests prove restart recovery and pre-compaction flush execution.

### Task 1.3: Session parent and entity routing

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** `openspec/specs/netclaw-session/spec.md`
**Surface area:** actor runtime
**Verification:** L2

Done when:
- [ ] `LlmAgentParentActor` wraps `GenericChildPerEntityParent`.
- [ ] Session extraction routes same-thread messages to same child actor.
- [ ] Multi-key-pattern support (Slack and timer patterns).
- [ ] Tests verify entity lifecycle and message routing.

### Task 1.4: Layered system prompt and personality

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** agent personality
**Verification:** L2

Done when:
- [ ] `~/.netclaw/` directory structure created on startup (soul/, projects/, environment/, schedules/, config/).
- [ ] System prompt assembled from layers: PERSONALITY.md → INSTRUCTIONS.md → USER.md → project AGENTS.md → session context.
- [ ] Missing layers handled gracefully.
- [ ] Tests for prompt assembly with missing layers and project overlay injection.

### Task 1.5: ACL and policy engine with tool grants

**PRD:** `docs/prd/PRD-002-gateway-security-envelope.md`
**OpenSpec:** `openspec/specs/netclaw-acl/spec.md`, `openspec/specs/netclaw-gateway-security/spec.md`
**Surface area:** security
**Verification:** L2

Done when:
- [ ] ACL parser supports channel rules, sender allowlists, mention/ambient mode, and tool grant categories (shell, web_search, web_fetch, github, mcp:{server}, config_write, schedule_write).
- [ ] Default deny enforced when no explicit allow.
- [ ] Self-configuration prohibition: agent cannot modify ACL/security through conversation.
- [ ] Invalid ACL blocks startup with actionable diagnostics.
- [ ] Shell execution boundaries enforced (SEC-009): timeout, output truncation, no stdin, working dir.
- [ ] Tool invocation audit logging.
- [ ] Policy decision tests cover all grant categories.

### Task 1.6: Tool framework and MEAI registration

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`, `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-tools/spec.md`, `openspec/specs/netclaw-model-providers/spec.md`
**Surface area:** tool framework
**Verification:** L2

Done when:
- [ ] Tool registry registers `AIFunction` definitions through `Microsoft.Extensions.AI`.
- [ ] Policy-filtered tool loading: session receives only tools matching ACL grants.
- [ ] Tool invocation audit logging (tool name, session ID, timestamp, allow/deny).
- [ ] Tool context added to session state at initialization.
- [ ] Tests for registration, policy filtering, and audit logging.

### Task 1.7: First-party tools (search, fetch, shell, GitHub)

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-tools/spec.md`
**Surface area:** tools
**Verification:** L2

Done when:
- [ ] Web search tool with Brave Search API and SearXNG backends, configurable via `netclaw.json`.
- [ ] Web fetch tool with HTML-to-text extraction and output truncation.
- [ ] Shell execution tool with timeout, output truncation, stdin closure, working directory.
- [ ] GitHub CLI tool via `gh` shell-out with structured output parsing and missing dependency handling.
- [ ] Tests for each tool with mocked HTTP/process dependencies.

### Task 1.8: Provider abstraction with MEAI and fallback

**PRD:** `docs/prd/PRD-005-model-provider-strategy.md`
**OpenSpec:** `openspec/specs/netclaw-model-providers/spec.md`
**Surface area:** provider integration
**Verification:** L2

Done when:
- [ ] `IChatClient` provider registration via DI (OpenRouter, Anthropic, OpenAI, Ollama).
- [ ] Primary + fallback model with automatic failover on rate limit/timeout/error.
- [ ] Tool calling through MEAI tool calling API.
- [ ] CI tests pass without live provider credentials.
- [ ] Tests for provider switching, fallback activation, tool calling round-trip.

### Task 1.9: MCP integration and Memorizer

**PRD:** `docs/prd/PRD-006-mcp-tool-integration.md`
**OpenSpec:** `openspec/specs/netclaw-mcp/spec.md`
**Surface area:** integration
**Verification:** L2

Done when:
- [ ] MCP server profiles (named, stdio/SSE transport, enable/disable).
- [ ] Tool discovery at startup: connect, list tools, register as MEAI definitions.
- [ ] Graceful degradation: unavailable server returns error, agent continues, reconnect on next call.
- [ ] Memorizer store/search/get cycle works through session.
- [ ] Tests for connection, discovery, policy gating, degradation.

### Task 1.10: Local memory (project registry, environment inventory)

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** agent memory
**Verification:** L2

Done when:
- [ ] Project registry (`projects/registry.json`): add, remove, list, validate paths, load at startup.
- [ ] Environment inventory (`environment/inventory.json`): scan for git, gh, claude, opencode, dotnet, node.
- [ ] Capability self-discovery at startup and on-demand rescan.
- [ ] Tests for project registry CRUD and environment scan.

### Task 1.11: Self-configuration through conversation

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`
**Surface area:** agent config
**Verification:** L2

Done when:
- [ ] Agent modifies personality, instructions, user preferences, project registry, and environment through conversation.
- [ ] Validation-before-write and atomic file writes (temp + rename).
- [ ] Prohibited modification enforcement: reject ACL, security, tool grants, exposure, credentials.
- [ ] Tests for allowed modifications, prohibited modifications, validation failures.

### Task 1.12: Scheduling system

**PRD:** `docs/prd/PRD-008-scheduling-and-periodic-tasks.md`
**OpenSpec:** `openspec/specs/netclaw-scheduling/spec.md`
**Surface area:** scheduling
**Verification:** L2

Done when:
- [ ] `ScheduleManagerActor` loads tasks from `schedules/tasks.json`, manages Akka timers.
- [ ] Chat-driven creation: interval and cron types, validate tool grants, persist task.
- [ ] Isolated execution: timer dispatches `SendUserMessage` with `schedule/{taskId}/{runTs}` entity key.
- [ ] Result reporting: post to configured Slack channel, silent-unless-notable mode.
- [ ] Guardrails: max concurrent (3), timeout (5min), consecutive failure auto-pause (5).
- [ ] Task management: list, pause, resume, delete via conversation and CLI.
- [ ] Tests for persistence, timer lifecycle, isolated execution, failure handling.

### Task 1.13: CLI scaffold with Cocona + Termina hosting

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md` (CLI-010, CLI-012)
**OpenSpec:** `openspec/specs/netclaw-cli/spec.md`
**OpenSpec Changes:** `openspec/changes/add-tui-adapter-and-config-hot-reload/` (Section 1)
**Surface area:** CLI framework
**Verification:** L1

Done when:
- [ ] Cocona and Termina package references added to `Directory.Packages.props` and `Netclaw.App.csproj`.
- [ ] `Program.cs` rewritten as Cocona entry point with DI registration.
- [ ] `RunCommand.cs` created for daemon mode (`netclaw run`).
- [ ] Termina wired as hosted service for TUI commands.
- [ ] `dotnet build` passes with new dependencies.

### Task 1.14: TUI chat adapter (`netclaw chat`)

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md` (CLI-011), `docs/prd/PRD-009-input-adapters-and-unified-input.md` (INPUT-005)
**OpenSpec:** `openspec/specs/netclaw-input-adapters/spec.md`, `openspec/specs/netclaw-cli/spec.md`
**OpenSpec Changes:** `openspec/changes/add-tui-adapter-and-config-hot-reload/` (Section 2)
**Surface area:** TUI + adapter
**Verification:** L3
**Wireframe:** `docs/ui/TUI-001-command-wireframes.md` (netclaw chat)

Done when:
- [ ] `TuiInputAdapter` implementing adapter contract (`SendUserMessage` with entity key `tui/{sessionId}`).
- [ ] `ChatCommand.cs` hosts actor system in-process and launches TUI.
- [ ] `ChatPage.cs` with `StreamingTextNode` (scrollable history) and `TextInputNode` (multi-line input).
- [ ] `ChatViewModel.cs` with session lifecycle and broadcast subscription.
- [ ] Inline tool activity panel (completed with duration, in-progress with spinner).
- [ ] MCP status indicator in status bar (green/yellow/red).
- [ ] E2E: user types → `SendUserMessage` → session actor → LLM → streaming response in TUI.

### Task 1.15: TUI onboarding wizard (`netclaw init`)

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md` (CLI-010)
**OpenSpec:** `openspec/specs/netclaw-onboarding/spec.md`, `openspec/specs/netclaw-cli/spec.md`
**OpenSpec Changes:** `openspec/changes/add-tui-adapter-and-config-hot-reload/` (Section 3)
**Surface area:** TUI + onboarding
**Verification:** L3
**Wireframe:** `docs/ui/TUI-001-command-wireframes.md` (netclaw init)

Done when:
- [ ] `InitCommand.cs` launches Termina wizard.
- [ ] `InitWizardPage.cs` with 7-step wizard layout (`PanelNode`, progress bar).
- [ ] `InitWizardViewModel.cs` with step state machine and back-navigation.
- [ ] Steps: LLM provider, Slack config, PostgreSQL, ACL bootstrap, MCP servers, exposure mode, health check.
- [ ] Config file written to `~/.netclaw/config/netclaw.json` on completion.

### Task 1.16: Plain CLI commands

**PRD:** `docs/prd/PRD-004-cli-onboarding-and-config.md`
**OpenSpec:** `openspec/specs/netclaw-cli/spec.md`
**OpenSpec Changes:** `openspec/changes/add-tui-adapter-and-config-hot-reload/` (Section 4)
**Surface area:** CLI
**Verification:** L1

Done when:
- [ ] `DoctorCommand.cs` — startup checks with remediation guidance, exit codes 0/1/2.
- [ ] `ConfigCommands.cs` — `config show` and `config validate`.
- [ ] `AclCommands.cs` — `acl validate`, `acl test`, `acl explain`.
- [ ] `ProjectCommands.cs` — `project list`, `project add`, `project remove`.
- [ ] `ScheduleCommands.cs` — `schedule list|show|pause|resume|delete`.
- [ ] Remaining commands: `environment scan|show`, `mcp list|validate|test`, `memory show`, `tools list|policy`, `test smoke`, `personality reset`.

### Task 1.17: Config hot-reload

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md` (FR-016)
**OpenSpec:** `openspec/specs/netclaw-config-hot-reload/spec.md`, `openspec/specs/netclaw-session/spec.md`
**OpenSpec Changes:** `openspec/changes/add-tui-adapter-and-config-hot-reload/` (Section 5)
**Surface area:** runtime config
**Verification:** L2

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

### Task 1.18: Conversational personality bootstrap

**PRD:** `docs/prd/PRD-007-agent-personality-and-local-memory.md`
**OpenSpec:** `openspec/specs/netclaw-agent-memory/spec.md`, `openspec/specs/netclaw-onboarding/spec.md`
**OpenSpec Changes:** `openspec/changes/add-tui-adapter-and-config-hot-reload/` (Section 6)
**Surface area:** onboarding
**Verification:** L2

Done when:
- [ ] First-run detection: trigger bootstrap when soul files don't exist on first `netclaw chat`.
- [ ] Bootstrap conversation: introduce, learn preferences, scan environment, write soul files, confirm.
- [ ] PERSONALITY.md, INSTRUCTIONS.md, USER.md written to config directory.
- [ ] Test: bootstrap triggers when files missing, skips when files exist.

### Task 1.19: Local E2E validation via TUI

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** all Phase 1 specs
**OpenSpec Changes:** `openspec/changes/add-tui-adapter-and-config-hot-reload/` (Section 7)
**Surface area:** end-to-end
**Verification:** L4

Done when:
- [ ] E2E: `netclaw chat` → session → tool call → streaming response.
- [ ] E2E: scheduled task → fresh session → result displayed.
- [ ] E2E: config change → hot-reload → policy refresh verified.
- [ ] CI tests pass without live provider credentials.

### Task 1.20: Slack Socket Mode adapter

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** `openspec/specs/netclaw-slack-socket/spec.md`, `openspec/specs/netclaw-input-adapters/spec.md`
**Surface area:** integration
**Verification:** L2

Done when:
- [ ] Socket Mode connection and event handling (`app_mention`, `message`).
- [ ] Entity key extraction: `{channelId}/{threadTs}`.
- [ ] Reply delivery: subscribe to session broadcasts, post replies to originating thread.
- [ ] Reconnection on disconnect.
- [ ] End-to-end test proves message → reply loop.

### Task 1.21: Full integration and acceptance

**PRD:** `docs/prd/PRD-001-netclaw-mvp.md`
**OpenSpec:** all Phase 1 specs
**Surface area:** end-to-end
**Verification:** L4

Done when:
- [ ] E2E: Slack message → session → tool call → reply in thread.
- [ ] E2E: scheduled task fires → fresh session → result posted to Slack.
- [ ] E2E: restart recovery preserves session context and scheduled tasks.
- [ ] CI test suite passes without live provider credentials.
- [ ] Deploy to pi1 and verify Slack interaction.

### Task 1.22: Spec sync and archive

**OpenSpec Changes:** `openspec/changes/expand-mvp-for-autonomous-agent-vision/`, `openspec/changes/add-tui-adapter-and-config-hot-reload/`
**Surface area:** process
**Verification:** L0

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
