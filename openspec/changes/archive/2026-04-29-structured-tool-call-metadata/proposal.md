## Why

Tool calls in Netclaw carry no structured metadata beyond the tool name and
arguments. This creates two problems: (1) after compaction or session
resumption, the agent loses its reasoning chain because tool call intent is
embedded in conversation history that gets compressed away (#751), and (2)
tools have fixed timeouts with no per-call override, making long-running
commands impossible without workarounds (#747). Adding a `ToolCallMeta`
envelope — injected into tool schemas transparently, stripped before dispatch
— solves both by giving every tool call a required rationale and optional
timeout hint without changing any tool implementation.

## What Changes

- Introduce a `ToolCallMeta` type carrying a required `Rationale` string, an
  optional `TimeoutHintSeconds`, and an optional `Background` flag.
- Augment every tool's JSON schema with meta fields before presenting to the
  LLM. The source generator (`NetclawToolGenerator`) injects them for native
  tools; `McpToolAdapter` injects them for MCP tools.
- Strip meta fields from tool arguments at the pipeline boundary before
  dispatching to tool implementations. Tools remain completely unaware.
- Persist extracted meta on `SerializableToolCall` (new protobuf field) so
  rationale survives journal replay and session recovery.
- Enrich `ToolAuditEntry` with `Rationale` and `TimeoutHintSeconds` for
  operational observability.
- Apply `TimeoutHintSeconds` in `SessionToolExecutionPipeline`, clamped to a
  configurable `MaxToolTimeoutSeconds` ceiling (default 600s).
- Preserve `_background` as an explicit-only signal for follow-on background
  execution. `_timeout_seconds` remains a synchronous timeout hint and MUST
  NOT auto-promote a tool call to background execution.
- Update `CompactionPromptBuilder` to render tool calls using rationale as the
  primary representation, replacing the current raw-arguments format. This
  provides high-signal breadcrumbs for session resumption.

## Capabilities

### New Capabilities
- `tool-call-metadata`: Cross-cutting metadata envelope for tool invocations,
  covering schema augmentation, pipeline extraction, persistence, audit
  enrichment, and compaction integration.

### Modified Capabilities
- `netclaw-tools`: Tool schemas gain injected meta fields; `ToolAuditEntry`
  gains `Rationale` and `TimeoutHintSeconds`; `ToolConfig` gains
  `MaxToolTimeoutSeconds`.
- `netclaw-session`: Compaction rendering of tool calls changes from raw
  `[Called tool: name(args)]` to rationale-based `→ name: "rationale"` format.
  `SerializableToolCall` gains a `MetaJson` protobuf field.

## Impact

- **Code**: `NetclawToolGenerator` (source generator), `McpToolAdapter`,
  `SessionToolExecutionPipeline`, `CompactionPromptBuilder`,
  `SerializableToolCall`, `ToolAuditEntry`, `ToolConfig`.
- **Persistence**: Additive protobuf field on `SerializableToolCall` —
  backwards compatible (existing journals deserialize with null meta).
- **Token cost**: Required `Rationale` adds ~20-50 tokens per tool call. Meta
  fields in tool schemas add ~60 tokens per tool definition in the system
  prompt.
- **Config schema**: `netclaw-config.v1.schema.json` gains
  `MaxToolTimeoutSeconds` under `tools`.
- **Security**: No audience changes. Meta fields are pure metadata with no
  privilege implications. Timeout ceiling prevents resource abuse.
- **Dependencies**: None — uses existing MEAI, protobuf-net, and source
  generator infrastructure.
