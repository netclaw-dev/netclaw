# Tasks: Expand MVP for Autonomous Agent Vision

## 1. Framework Protocol and Persistence Envelopes

- [ ] 1.1 Implement `SendUserMessage`, `TurnRecorded`, `SessionCompacted`,
  `TurnBroadcast`, and `CompactionBroadcast` concrete types with protobuf-net
  serialization.
- [ ] 1.2 Implement `SerializableChatMessage` framework-owned chat type (no
  direct persistence of `Microsoft.Extensions.AI` types).
- [ ] 1.3 Implement `SessionMessageExtractor` supporting entity key patterns:
  `{channelId}/{threadTs}` for Slack, `schedule/{taskId}/{runTs}` for timers.
- [ ] 1.4 Write integration tests verifying event serialization round-trip and
  entity key extraction for both Slack and timer patterns.

## 2. Session Actor Core

- [ ] 2.1 Implement `LlmSessionActor` with persistent state recovery from
  PostgreSQL journal/snapshots.
- [ ] 2.2 Implement turn processing loop: receive `SendUserMessage`, invoke
  `IChatClient`, persist `TurnRecorded`, emit `TurnBroadcast` via pub/sub.
- [ ] 2.3 Implement snapshot strategy and compaction trigger via
  `SummarizingChatReducer`.
- [ ] 2.4 Implement pre-compaction memory flush: silent agentic turn that saves
  durable memories before context resets (spec: netclaw-agent-memory).
- [ ] 2.5 Add source metadata to `SendUserMessage` (adapter type, sender
  identity, channel, timestamp) per netclaw-input-adapters spec.
- [ ] 2.6 Write integration tests proving restart recovery preserves context
  and pre-compaction flush executes before compaction.

## 3. Session Parent and Entity Routing

- [ ] 3.1 Implement `LlmAgentParentActor` wrapping `GenericChildPerEntityParent`.
- [ ] 3.2 Implement session extraction routing same-thread messages to same
  child actor using `SessionMessageExtractor`.
- [ ] 3.3 Write tests verifying entity lifecycle, message routing, and
  multi-key-pattern support.

## 4. Layered System Prompt and Personality

- [ ] 4.1 Create `~/.netclaw/` standard directory structure (soul/, projects/,
  environment/, schedules/, config/) with creation-on-startup behavior.
- [ ] 4.2 Implement system prompt assembly from layered sources:
  PERSONALITY.md → INSTRUCTIONS.md → USER.md → project AGENTS.md → session
  context.
- [ ] 4.3 Implement project AGENTS.md overlay loading from registered project
  paths.
- [ ] 4.4 Write tests for prompt assembly with missing layers and project
  overlay injection.

## 5. ACL and Policy Engine

- [ ] 5.1 Implement ACL configuration parser supporting channel rules, sender
  allowlists, mention/ambient mode, and tool grant categories (shell,
  web_search, web_fetch, github, mcp:{server}, config_write, schedule_write).
- [ ] 5.2 Implement default-deny evaluation: deny when no explicit allow exists.
- [ ] 5.3 Implement self-configuration prohibition: agent cannot modify ACL or
  security files through conversation.
- [ ] 5.4 Implement startup validation: invalid ACL blocks startup with
  actionable diagnostics.
- [ ] 5.5 Write policy decision tests covering allow/deny reason codes for all
  tool grant categories.

## 6. Tool Framework and MEAI Registration

- [ ] 6.1 Implement tool registry that registers `AIFunction` definitions
  through `Microsoft.Extensions.AI`.
- [ ] 6.2 Implement policy-filtered tool loading: session receives only tools
  matching its ACL grants.
- [ ] 6.3 Implement tool invocation audit logging (tool name, session ID,
  timestamp, allow/deny).
- [ ] 6.4 Add tool context to session state at initialization.
- [ ] 6.5 Write tests for tool registration, policy filtering, and audit
  logging.

## 7. First-Party Tools

- [ ] 7.1 Implement web search tool with Brave Search API backend (thin
  `HttpClient` wrapper, structured JSON response).
- [ ] 7.2 Implement web search SearXNG backend as alternative (same tool
  interface, different backend).
- [ ] 7.3 Implement configurable search backend selection via
  `config/netclaw.json`.
- [ ] 7.4 Implement web fetch tool (URL retrieval, HTML-to-text extraction,
  output truncation).
- [ ] 7.5 Implement shell execution tool (`System.Diagnostics.Process`
  wrapper, timeout enforcement, output truncation, stdin closure, working
  directory from project registry).
- [ ] 7.6 Implement GitHub CLI tool (shell-out to `gh`, structured output
  parsing, missing dependency handling).
- [ ] 7.7 Write tests for each tool with mocked HTTP/process dependencies.

## 8. MCP Integration and Memorizer

- [ ] 8.1 Implement MCP server profile configuration (named profiles, transport
  type stdio/SSE, enable/disable).
- [ ] 8.2 Implement MCP tool discovery at startup: connect to enabled servers,
  list tools, register as MEAI tool definitions.
- [ ] 8.3 Implement graceful degradation: unavailable server returns error,
  agent continues, reconnect on next call.
- [ ] 8.4 Implement MCP validation command (`netclaw mcp validate`).
- [ ] 8.5 Write tests for MCP connection, tool discovery, policy gating, and
  degradation.

## 9. Local Memory System

- [ ] 9.1 Implement project registry (`projects/registry.json`): add, remove,
  list, validate paths, load at startup.
- [ ] 9.2 Implement environment inventory (`environment/inventory.json`): scan
  for git, gh, claude, opencode, dotnet, node; check versions and credentials.
- [ ] 9.3 Implement capability self-discovery at startup and on-demand rescan.
- [ ] 9.4 Write tests for project registry CRUD and environment scan accuracy.

## 10. Self-Configuration Through Conversation

- [ ] 10.1 Implement self-configuration tool: agent modifies personality,
  instructions, user preferences, project registry, and environment inventory
  through conversation.
- [ ] 10.2 Implement validation-before-write and atomic file writes (temp +
  rename).
- [ ] 10.3 Implement prohibited modification enforcement: reject ACL, security
  policy, tool grants, exposure mode, and credential changes.
- [ ] 10.4 Write tests for allowed modifications, prohibited modifications, and
  validation failures.

## 11. Scheduling System

- [ ] 11.1 Implement `ScheduleManagerActor` that loads tasks from
  `schedules/tasks.json` at startup and manages Akka timers.
- [ ] 11.2 Implement chat-driven task creation: parse interval and cron
  schedule types from conversation, validate tool grants, persist task.
- [ ] 11.3 Implement isolated task execution: timer fires dispatch
  `SendUserMessage` to session parent with fresh entity key
  (`schedule/{taskId}/{runTs}`).
- [ ] 11.4 Implement result reporting: post execution results to configured
  Slack channel, support silent-unless-notable mode.
- [ ] 11.5 Implement guardrails: max concurrent executions (default 3),
  execution timeout (default 5min), consecutive failure auto-pause (default 5).
- [ ] 11.6 Implement task management: list, pause, resume, delete via
  conversation and CLI.
- [ ] 11.7 Write tests for schedule persistence, timer lifecycle, isolated
  execution, and failure handling.

## 12. Slack Socket Mode Adapter

- [ ] 12.1 Implement Slack Socket Mode connection and event handling
  (`app_mention`, `message` events).
- [ ] 12.2 Implement entity key extraction from Slack events
  (`{channelId}/{threadTs}`).
- [ ] 12.3 Implement reply delivery: subscribe to session broadcasts, post
  replies to originating Slack thread.
- [ ] 12.4 Implement reconnection on disconnect.
- [ ] 12.5 Write end-to-end test proving message → reply loop.

## 13. Provider Abstraction

- [ ] 13.1 Wire `Microsoft.Extensions.AI` `IChatClient` provider registration
  with DI (OpenRouter, Anthropic, OpenAI, Ollama profiles).
- [ ] 13.2 Implement primary + fallback model configuration with automatic
  failover on rate limit, timeout, or provider error.
- [ ] 13.3 Implement tool calling support through MEAI tool calling API.
- [ ] 13.4 Write tests for provider switching, fallback activation, and tool
  calling round-trip.

## 14. CLI Onboarding and Management

- [ ] 14.1 Implement `netclaw init` guided wizard (LLM provider, Slack
  credentials, PostgreSQL connection, ACL bootstrap, MCP config, exposure mode,
  health check).
- [ ] 14.2 Implement `netclaw config show|validate` and
  `netclaw acl validate|test|explain`.
- [ ] 14.3 Implement `netclaw project list|add|remove` and
  `netclaw environment scan|show`.
- [ ] 14.4 Implement `netclaw schedule list|show|pause|resume|delete`.
- [ ] 14.5 Implement `netclaw mcp list|validate|test`.
- [ ] 14.6 Implement `netclaw personality reset` and `netclaw memory show`.
- [ ] 14.7 Implement `netclaw gateway status|doctor`.
- [ ] 14.8 Implement `netclaw test smoke --provider ollama`.

## 15. Conversational Personality Bootstrap

- [ ] 15.1 Implement first-run detection: trigger bootstrap when soul files
  don't exist.
- [ ] 15.2 Implement bootstrap conversation flow: introduce, learn preferences,
  scan environment, write soul files, confirm readiness.
- [ ] 15.3 Write test for bootstrap trigger and soul file creation.

## 16. Spec Sync and Validation

- [ ] 16.1 Sync delta specs to main specs using `openspec sync` or equivalent.
- [ ] 16.2 Run `openspec validate --all --no-interactive` and fix any issues.
- [ ] 16.3 Update `IMPLEMENTATION_PLAN.md` Phase 1+ with tasks from this
  change plan.

## 17. Integration and Acceptance

- [ ] 17.1 End-to-end test: Slack message → session → tool call → reply in
  thread.
- [ ] 17.2 End-to-end test: scheduled task fires → fresh session → result
  posted to Slack.
- [ ] 17.3 End-to-end test: restart recovery preserves session context and
  scheduled tasks.
- [ ] 17.4 Verify CI test suite passes without live provider credentials.
