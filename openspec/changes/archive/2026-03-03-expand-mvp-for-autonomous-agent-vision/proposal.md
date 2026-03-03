## Why

Netclaw's original planning scope described a narrow "Slack chat assistant with
ACL." The product vision has been significantly expanded: Netclaw is an
always-on autonomous operations agent that maintains its own personality,
discovers its environment, manages its schedule, uses local tools, and modifies
its own configuration through conversation. All existing PRDs (001-006) have
been revised and three new PRDs (007-009) have been created. The OpenSpec
capability specs must now match this expanded vision so implementation tasks
are RALPH-consumable.

Source PRDs: PRD-001 (revised), PRD-002 (revised), PRD-003 (deferred Phase 5),
PRD-004 (revised), PRD-005 (revised), PRD-006 (revised), PRD-007 (new),
PRD-008 (new), PRD-009 (new).

See `PROJECT_CONTEXT.md` for the full product vision.
See `docs/research/agent-patterns.md` for the research informing these changes.

## What Changes

- Add agent personality system: layered system prompt from soul files
  (PERSONALITY.md, INSTRUCTIONS.md, USER.md), conversational personality
  bootstrap, project context overlays
- Add local memory system: project registry (JSON), environment inventory
  (JSON), standard `~/.netclaw/` data directory
- Add capability self-discovery: scan for installed tools (git, gh, claude,
  opencode, dotnet), git credentials, MCP server reachability
- Add self-configuration: agent modifies own config through conversation
  within safety boundaries (cannot modify ACL/security)
- Add pre-compaction memory flush: silent agentic turn saves durable memories
  before context resets
- Add first-party tool access: web search (Brave Search API / SearXNG),
  web fetch, shell execution, GitHub via `gh` CLI — all policy-gated
- Add tool registration via Microsoft.Extensions.AI tool calling API
- Add chat-driven scheduling: create/list/pause/delete scheduled tasks through
  conversation, persist as JSON, execute via Akka timers in fresh sessions
- Add unified input adapter architecture: transport-agnostic session commands,
  source metadata, entity key routing patterns for Slack and timer adapters
- Expand ACL with tool grant categories (shell, web_search, web_fetch, github,
  mcp:{server}, config_write, schedule_write)
- Expand security envelope with self-config safety rules (SEC-008) and shell
  execution boundaries (SEC-009)
- Expand MCP role: Memorizer as external memory tier, tool discovery and
  registration at startup, graceful degradation
- Expand provider strategy: Microsoft.Extensions.AI abstraction, primary +
  fallback model configuration, tool calling support
- Expand CLI with project/environment/memory/schedule management commands
  and two-phase onboarding (CLI wizard + conversational personality bootstrap)
- Defer ops console implementation to Phase 5 (spec/mockup only in MVP)

## Capabilities

### New Capabilities

- `netclaw-agent-memory`: Agent personality (soul files), local memory (project
  registry, environment inventory), capability self-discovery,
  self-configuration through conversation, pre-compaction memory flush,
  standard config directory. Source: PRD-007.
- `netclaw-scheduling`: Chat-driven scheduled task creation, persistence,
  isolated execution via Akka timers, result reporting, failure handling and
  guardrails. Source: PRD-008.
- `netclaw-input-adapters`: Unified input architecture, transport-agnostic
  session commands, source metadata, entity key routing, Slack Socket Mode
  adapter, internal timer adapter. Source: PRD-009.
- `netclaw-tools`: First-party tool access (web search, web fetch, shell,
  GitHub CLI), tool registration with MEAI, policy-gated invocation,
  configurable search backend. Source: PRD-001 FR-011, PRD-002 SEC-009,
  PRD-007.

### Modified Capabilities

- `netclaw-session`: Add pre-compaction memory flush trigger, pub/sub broadcast
  clarification for multi-adapter delivery, tool context in session state.
- `netclaw-acl`: Add tool grant categories (shell, web_search, web_fetch,
  github, mcp:{server}, config_write, schedule_write), self-config prohibition
  rules.
- `netclaw-gateway-security`: Add self-configuration safety rules (SEC-008),
  shell execution boundaries (SEC-009), tool invocation audit records.
- `netclaw-mcp`: Add Memorizer as external memory tier, tool discovery and
  registration at startup, graceful degradation on server unavailability.
- `netclaw-model-providers`: Add Microsoft.Extensions.AI (IChatClient)
  abstraction, primary + fallback model configuration, tool calling support
  through MEAI.
- `netclaw-cli`: Add project/environment/memory/schedule management commands,
  two-phase onboarding (CLI wizard + conversational personality bootstrap),
  environment scan command.
- `netclaw-onboarding`: Add Phase 2 conversational personality bootstrap,
  environment discovery during onboarding, project registration.
- `netclaw-operator-ui`: Defer implementation to Phase 5, add memory/schedule
  screen definitions for future UI, clarify MVP deliverable is spec/mockup only.

## Impact

- **Actor framework**: New actor types needed (ScheduleManagerActor, tool
  execution actors). Session actor gains tool context and pre-compaction flush.
- **Persistence**: Scheduled tasks persisted as files on disk (not in Akka
  journal). Session actor persistence unchanged.
- **Configuration**: New `~/.netclaw/` directory structure with soul files,
  project registry, environment inventory, schedules, and config.
- **Dependencies**: Brave Search API client (thin HttpClient wrapper),
  HTML-to-text library for web fetch. Microsoft.Extensions.AI already in use.
- **Security surface**: Shell execution, web fetch, and self-configuration
  create new attack surfaces requiring policy gates.
- **CLI**: Significant expansion of command surface. All new commands are
  read-only by default.
- **Testing**: Tool mocks/fakes needed for CI. Scheduled task execution tests
  need timer simulation.
