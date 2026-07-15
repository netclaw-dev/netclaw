## REMOVED Requirements

### Requirement: Audit entry enrichment

**Reason**: `ToolAuditEntry` was connected only to `NullToolAuditLogger` in the shipped runtime, so the requirement described discarded diagnostics rather than production behavior. Tool rationale and timeout metadata remain persisted on `SerializableToolCall.MetaJson` and visible in the existing session tool-call transcript.

**Migration**: Use the canonical `ToolCallOutput`/`ToolResultOutput` session transcript and persisted `ToolCallMeta`; there is no external C# audit extension contract because MCP is the extension boundary.
