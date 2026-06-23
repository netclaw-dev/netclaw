# SPEC-010: Testing and Smoke Strategy

Source PRDs: `PRD-001`, `PRD-002`, `PRD-004`, `PRD-005`, `PRD-006`, `PRD-007`, `PRD-008`, `PRD-009`

## Purpose

Define test categories so CI remains provider-independent while local smoke
tests can validate real provider integrations.

## Test Categories

### Category A: Unit Tests (CI required)

- pure logic and data model tests
- no network, no external services

### Category B: Actor Integration Tests (CI required)

- actor lifecycle and persistence behavior using in-memory/fake dependencies
- provider behavior simulated via fake chat client/provider abstractions

### Category C: Contract Tests (CI required)

- provider adapter contract behavior against fakes/stubs
- ACL/policy behavior around tool and provider invocations

### Category D: Live Smoke Tests (CI optional)

- explicit opt-in tests using real endpoints (for example, local Ollama)
- intended for developer or pre-release validation

## Critical Producer/Consumer Contract Inventory

The contracts below are the minimum cross-boundary producer/consumer pairs that
must stay named in planning and tests. A row is complete only when the producer
emits the canonical representation consumed by the downstream runtime path, and
the listed proof covers both a positive path and a relevant negative path. If the
proof is not complete yet, the gap is assigned to an explicit `NOW` task in
`IMPLEMENTATION_PLAN.md`.

| Contract surface | Producer | Downstream consumer | Canonical representation | Current proof or explicit `NOW` gap |
|------------------|----------|---------------------|--------------------------|--------------------------------------|
| Config editor -> runtime channel options | `ChannelsConfigViewModel` writes `netclaw.json` and `secrets.json` for Slack, Discord, and Mattermost | Daemon configuration binding into `SlackChannelOptions`, `DiscordChannelOptions`, `MattermostChannelOptions`, then adapter ACL policies | `AllowedChannelIds` are provider-native IDs; Slack IDs are `C...` or `G...`, Discord IDs are snowflake strings, Mattermost IDs are Mattermost channel IDs. `ChannelAudiences` uses those same IDs plus reserved `dm`; values are lowercase `personal`, `team`, or `public`. Secrets stay in `secrets.json` and are preserved when blank on re-entry. | `src/Netclaw.Cli.Tests/Tui/Config/ChannelsConfigViewModelTests.cs` proves Slack name -> ID persistence, audience remapping, unresolved Slack/Discord/Mattermost rejection, and secret preservation. Full editor-output -> runtime-options binding remains an explicit gap in Task 3.1. |
| Channel events -> ACL and gateway routing | Slack, Discord, and Mattermost adapters produce inbound provider messages and gateway route messages | `SlackAclPolicy`, `DiscordAclPolicy`, `MattermostAclPolicy`, and gateway actors before session delivery | Inbound messages carry provider-native channel ID, provider-native sender ID, accurate DM flag, source kind, and channel audience lookup keys matching configured IDs. Session identity remains `{channelId}/{threadTs}` or provider equivalent. | `src/Netclaw.Actors.Tests/Channels/Contracts/AclPolicyContractTests.cs`, `SlackAclContractTests.cs`, `DiscordAclContractTests.cs`, and `MattermostAclContractTests.cs` prove allowed, denied, DM, audience override, and invalid audience paths. `GatewayRoutingContractTests.cs` plus provider gateway contract tests prove denied messages are not routed and allowed messages are routed. |
| Scheduler -> delivery gateway | `SetReminderTool` and reminder persistence write `ReminderDefinition.Delivery` and later emit trusted delivery messages | Reminder execution actor and provider session binding actors that deliver without re-running inbound ACL | `Delivery.Kind` is `Channel` for channel delivery, `Delivery.Transport` is the lowercase provider key such as `slack`, and `Delivery.Address` is a canonical provider channel/user ID resolved before persistence. Runtime trusted delivery uses the stored target rather than a display name. | `src/Netclaw.Daemon.Tests/Reminder/ReminderTargetResolutionPathTests.cs` proves display target resolution to canonical channel/user IDs and unresolved target rejection. `src/Netclaw.Actors.Tests/Reminders/ReminderExecutionActorTests.cs` proves delivery success/failure reporting. Full gateway-chain and no-inbound-ACL re-entry coverage remains an explicit gap in Task 5.3. |
| Tool schemas -> model/tool dispatcher | Built-in tool registrations and MCP tool adapters expose tool declarations and schemas | Provider serializers, `SessionToolExecutionPipeline`, `McpToolAdapter`, and MCP client manager | Model-facing tools serialize as OpenAI-compatible function tools with stable names, descriptions, JSON Schema parameters, and required fields. MCP tool names use `server/tool`. Dispatcher arguments preserve schema-declared string values and reconstruct structured JSON values only when the schema requires them. | `src/Netclaw.Daemon.Tests/Configuration/OpenAiCompatibleChatClientTests.cs` proves OpenAI function-tool serialization and tool-call history shape. `src/Netclaw.Daemon.Tests/Mcp/SmokeMcpServerArgumentCoercionTests.cs` proves schema-driven MCP argument reconstruction over the real stdio JSON-RPC path. Approval allow/deny/prompt and malformed metadata coverage remains an explicit gap in Task 4.2. |
| Memory persistence -> prompt assembly | Memory curation, SQLite memory store, session events, and compaction events persist memory and conversation state | `SQLiteMemoryRecallCoordinator`, `SessionMessageAssembler`, and system prompt/session state assembly | Persisted memory uses framework-owned SQLite records and wire enum strings such as trust audience wire values. Session history uses `SerializableChatMessage` records, not provider SDK chat types. Recall appears as volatile context/nudges and does not mutate the stable system prompt prefix. | `src/Netclaw.Actors.Tests/Memory/SQLiteMemoryStoreTests.cs` proves memory persistence/search filtering and audience boundaries. `src/Netclaw.Actors.Tests/Memory/MemoryRedesignedEvalSuiteTests.cs` proves formation -> persistence -> recall. `src/Netclaw.Actors.Tests/Sessions/SessionMessageAssemblerTests.cs`, `SessionStateTests.cs`, and `src/Netclaw.Actors.Tests/Protocol/SerializationRoundTripTests.cs` prove prompt assembly placement and serialization-safe session records. Restart/recovery and corrupt/missing state coverage remains an explicit gap in Task 5.2. |

## CI Rules

- required CI pipeline executes categories A-C only
- CI must pass without provider credentials
- live smoke tests are excluded by default from required CI jobs

## Smoke Rules

- smoke tests require explicit command invocation
- smoke tests fail fast with clear remediation if endpoint is unreachable
- smoke tests produce concise health report for provider connectivity

## Local Smoke Profile (Developer Default)

- provider: `ollama`
- endpoint: `http://my-gpu-server:11434` (Tailscale network)
- model: `qwen3:30b`
- fallback model: `qwen3:14b`

These settings are for local development and pre-release validation only, not
required CI.

Reference profile snippet:

- `docs/spec/examples/local-dev-provider-profile.jsonc`
