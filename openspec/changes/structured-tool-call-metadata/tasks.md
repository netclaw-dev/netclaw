## 1. Core Type and Persistence

- [ ] 1.1 Create `ToolCallMeta` record type in `Netclaw.Tools.Abstractions` with `Rationale` (string?), `TimeoutHintSeconds` (int?), `Background` (bool) properties and JSON serialization support
- [ ] 1.2 Add `[ProtoMember(4)] string? MetaJson` to `SerializableToolCall` in `Netclaw.Actors/Protocol/SerializableChatMessage.cs`
- [ ] 1.3 Update `ChatMessageConverter` to populate `MetaJson` from extracted meta during `FromAiMessage` and restore it during `ToAiMessage`
- [ ] 1.4 Unit test: `SerializableToolCall` round-trip with and without `MetaJson` (backwards compatibility)

## 2. Source Generator Schema Augmentation

- [ ] 2.1 Modify `NetclawToolGenerator` to append `_rationale` (required string), `_timeout_seconds` (optional integer), and `_background` (optional boolean) properties to every generated tool `inputSchema`
- [ ] 2.2 Unit test: generated schema includes meta properties with correct types, descriptions, and required status

## 3. MCP Adapter Schema Augmentation

- [ ] 3.1 Extend `McpToolAdapter.SanitizeSchema()` to inject the three meta properties into MCP tool schemas
- [ ] 3.2 Strip `_rationale`, `_timeout_seconds`, and `_background` from argument dictionaries before forwarding to `IMcpToolInvoker`
- [ ] 3.3 Add collision detection: log warning if an MCP tool's original schema already defines a `_rationale`, `_timeout_seconds`, or `_background` parameter
- [ ] 3.4 Unit test: MCP tool schema gains meta fields after sanitization
- [ ] 3.5 Unit test: meta fields stripped from arguments before MCP invocation

## 4. Pipeline Meta Extraction and Timeout Application

- [ ] 4.1 Create a static `ToolCallMetaExtractor` utility that extracts `_rationale`, `_timeout_seconds`, `_background` from a `FunctionCallContent.Arguments` dictionary, returns a `ToolCallMeta` and a cleaned argument dictionary
- [ ] 4.2 Integrate `ToolCallMetaExtractor` into `SessionToolExecutionPipeline.ExecuteSingleToolAsync` — extract meta before dispatch, pass cleaned arguments to executor
- [ ] 4.3 Apply timeout from meta: clamp `_timeout_seconds` between tool default floor and `ToolConfig.MaxToolTimeoutSeconds` ceiling; use clamped value for `CancellationTokenSource`
- [ ] 4.4 Preserve `_background` as an explicit-only signal: log when background was requested; do not infer background execution from `_timeout_seconds` (actual routing deferred to background jobs change)
- [ ] 4.5 Pass extracted `ToolCallMeta` to audit entry creation
- [ ] 4.6 Unit test: meta extraction produces correct `ToolCallMeta` and clean arguments
- [ ] 4.7 Unit test: timeout clamping — within range, above ceiling, below floor, absent
- [ ] 4.8 Unit test: `_timeout_seconds` alone does not trigger background signaling

## 5. Audit Entry Enrichment

- [ ] 5.1 Add `string? Rationale` and `int? TimeoutHintSeconds` to `ToolAuditEntry` in `IToolExecutor.cs`
- [ ] 5.2 Update all `ToolAuditEntry` creation sites in `SessionToolExecutionPipeline` to populate new fields from extracted `ToolCallMeta`
- [ ] 5.3 Unit test: audit entry contains rationale and timeout hint when provided

## 6. Compaction Integration

- [ ] 6.1 Modify `CompactionPromptBuilder` tool call rendering: when `SerializableToolCall.MetaJson` contains a rationale, render as `→ {tool_name}: "{rationale}"` instead of `[Called tool: {tool_name}({args})]`
- [ ] 6.2 Preserve fallback to raw-arguments format when `MetaJson` is null (legacy messages)
- [ ] 6.3 Unit test: compaction prompt renders rationale-based format for meta-bearing tool calls
- [ ] 6.4 Unit test: compaction prompt renders raw-arguments format for legacy tool calls

## 7. Configuration

- [ ] 7.1 Add `MaxToolTimeoutSeconds` (int, default 600) to `ToolConfig`
- [ ] 7.2 Update `netclaw-config.v1.schema.json` with the new property including `minimum: 1` and a `default` value for `SchemaFixResolver` compatibility
- [ ] 7.3 Verify `ConfigSchemaDoctorCheck` passes with and without the new properties

## 8. Spec and Documentation Sync

- [ ] 8.1 Run `openspec sync` to apply delta specs to `netclaw-tools` and `netclaw-session` main specs after implementation is verified
- [ ] 8.2 Run `dotnet slopwatch analyze` — no new violations
