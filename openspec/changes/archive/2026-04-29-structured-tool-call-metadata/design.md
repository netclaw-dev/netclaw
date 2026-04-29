## Context

Tool calls in Netclaw flow through a multi-layer pipeline:

1. The LLM emits `FunctionCallContent` (MEAI) with `Name`, `CallId`, and
   `Arguments` (a dictionary).
2. `SessionToolExecutionPipeline` orchestrates parallel execution, timeout
   enforcement, and approval gating.
3. `DispatchingToolExecutor` resolves the tool via `ToolRegistry`, checks
   `ToolAccessPolicy`, and invokes `INetclawTool.ExecuteAsync`.
4. Results flow back through `SerializableChatMessage` for persistence and
   `CompactionPromptBuilder` for context management.

Tool schemas are generated in two ways: the `NetclawToolGenerator` Roslyn
source generator emits JSON schemas for native tools at compile time, and
`McpToolAdapter` wraps MCP server tool schemas at runtime.

Today, tool calls carry no metadata beyond arguments. This means:

- **No rationale**: after compaction, tool calls render as
  `[Called tool: shell_execute({"Command":"dotnet test"})]` — the agent's
  intent is lost.
- **No timeout control**: `ShellTool` enforces a fixed 60s timeout
  (`ToolConfig.ShellTimeoutSeconds`); the pipeline enforces 90s
  (`SessionConfig.ToolExecutionTimeout`). No per-call override exists.
- **No background signal**: there is no way for the LLM to indicate a command
  should run asynchronously (background job execution is a follow-on change).

### Prior art

- **Spring AI Tool Argument Augmenter**: injects extra fields into tool schemas
  at the provider level, strips them before dispatch. The tool implementation
  is unaware. Validated in production.
- **Claude Code**: the `Bash` tool has a required `description` field —
  effectively a rationale. Used for auditability and user-facing display.
- **MCP `_meta`**: the protocol reserves a `_meta` field on `tools/call` for
  per-call metadata (correlation IDs, locale, etc.). Our approach aligns with
  this pattern at the application layer.

## Goals / Non-Goals

**Goals:**

- Every tool call carries a required rationale that persists through
  journal replay and survives compaction as a high-signal breadcrumb.
- The LLM can request a per-call timeout, clamped to a configurable ceiling.
- The LLM can signal that a tool call should execute in the background
  (consumed by a follow-on change).
- Tool implementations (native and MCP) require zero changes.
- Existing persisted sessions deserialize cleanly (additive schema change).

**Non-Goals:**

- Background job execution infrastructure (separate OpenSpec change).
- Per-audience timeout ceilings (shell's Personal-only gate is sufficient).
- Confidence scores, chain-of-thought steps, or other rich reasoning
  structures beyond a single rationale string.
- Rationale-based routing or tool selection (rationale is observability and
  compaction data, not control flow).

## Decisions

### D1: Hybrid schema augmentation with pipeline extraction

**Choice**: Inject meta fields into tool JSON schemas before the LLM sees them.
At the pipeline boundary, extract meta fields from the argument dictionary
before dispatching to the tool. Persist extracted meta separately on
`SerializableToolCall.MetaJson`.

**Alternatives considered**:

- *Envelope alongside arguments*: Would require LLM providers to support a
  separate `_meta` field on tool calls. Anthropic's API puts everything in
  `input` — no separate meta channel exists. Would not work without provider
  changes.
- *Inject into tool TParams*: Would require every tool author to accept and
  ignore meta fields. Breaks the tool abstraction boundary.

**Rationale**: The Spring AI pattern is proven. Schema injection is transparent
to tool authors. Pipeline extraction is a single chokepoint
(`SessionToolExecutionPipeline`). The LLM naturally populates injected fields
as part of normal tool calling.

### D2: Rationale is required

**Choice**: `Rationale` is a required field in the injected schema.

**Alternatives considered**:

- *Optional*: saves ~20-50 tokens per call but the LLM may skip it during
  complex reasoning when breadcrumbs matter most.

**Rationale**: The compaction and session resumption value depends on rationale
being consistently present. A missing rationale after compaction leaves the
agent blind to its own prior intent. Token cost is modest relative to the
context window.

### D3: Meta field names use underscore prefix convention

**Choice**: Injected fields are named `_rationale`, `_timeout_seconds`, and
`_background` to distinguish them from tool-defined parameters.

**Alternatives considered**:

- *No prefix (Rationale, TimeoutHintSeconds)*: risk of collision with tool
  parameter names, especially on MCP tools with unknown schemas.
- *Nested `_meta` object*: cleaner namespace isolation but some LLMs handle
  nested objects in tool calls less reliably than flat fields.

**Rationale**: Underscore prefix is a lightweight convention that avoids
collisions without nesting. The extraction logic can match on prefix for
forward compatibility if more meta fields are added later.

### D4: Timeout clamped to config ceiling with tool-level floor

**Choice**: `_timeout_seconds` is clamped to
`ToolConfig.MaxToolTimeoutSeconds` (ceiling, default 600s). The floor is the
tool's existing default timeout (60s for shell, 90s for general). Values below
the floor are ignored (the default applies).

**Rationale**: Prevents the LLM from requesting absurd timeouts while still
allowing meaningful extension. The floor prevents the LLM from accidentally
shortening timeouts below safe defaults.

### D5: Background signaling is explicit-only

**Choice**: `_background` is the only signal that requests background
execution. `_timeout_seconds` remains a synchronous timeout hint and SHALL NOT
auto-promote a call to background execution.

**Alternatives considered**:

- *Timeout-threshold promotion*: convenient for long-running commands, but it
  conflates "wait longer for this synchronous call" with "run this later and
  give me a handle".

**Rationale**: Timeout selection and execution mode are separate user intents.
Keeping `_timeout_seconds` sync-only avoids surprising behavior where asking
for more patience changes delivery semantics. Background execution remains an
explicit opt-in.

### D6: MetaJson as opaque JSON on SerializableToolCall

**Choice**: Add `[ProtoMember(4)] string? MetaJson` to `SerializableToolCall`.
Store the full `ToolCallMeta` as a JSON string rather than individual protobuf
fields.

**Alternatives considered**:

- *Individual protobuf fields for each meta property*: More type-safe but
  requires protobuf schema changes every time a meta field is added.
- *Store only rationale (not full meta)*: Loses timeout and background info
  from the persisted record.

**Rationale**: Opaque JSON is forward-compatible — adding new meta fields
doesn't require protobuf schema migration. The `MetaJson` field is nullable,
so existing messages deserialize cleanly with `null`. The pipeline can parse
it when needed (compaction, audit) without changing the wire format.

### D7: Compaction renders rationale as primary tool call representation

**Choice**: `CompactionPromptBuilder` renders tool calls with rationale as:
```
→ shell_execute: "running full test suite to verify refactor"
```
instead of the current:
```
[Called tool: shell_execute({"Command":"dotnet test --no-build"})]
```

When `MetaJson` is null (legacy messages), fall back to the current format.

**Rationale**: Rationale is dramatically more useful for session resumption
than raw arguments. The compaction prompt exists to help the LLM reconstruct
context efficiently — intent is higher signal than parameter values.

### D8: Source generator injects meta fields at schema emit time

**Choice**: `NetclawToolGenerator` appends the three meta properties
(`_rationale`, `_timeout_seconds`, `_background`) to the generated
`inputSchema` JSON for every tool. These properties are not part of the
tool's `TParams` record and are not passed to `ExecuteAsync`.

**Rationale**: Compile-time injection means native tools always have meta
fields without runtime overhead. The generator already builds the JSON schema
from `TParams` constructor parameters — appending additional properties is a
straightforward extension.

### D9: McpToolAdapter injects meta fields at schema sanitize time

**Choice**: `McpToolAdapter.SanitizeSchema()` gains an additional phase that
appends the same three meta properties to the MCP tool's `inputSchema`. Before
forwarding arguments to the MCP server (via `IMcpToolInvoker`), the adapter
strips any `_rationale`, `_timeout_seconds`, and `_background` keys from the
argument dictionary.

**Rationale**: MCP tools have externally-defined schemas that we cannot modify
at source. Runtime injection at the adapter level is the only option. The
adapter already sanitizes schemas (removing nullable unions, etc.), so this is
a natural extension point.

## Risks / Trade-offs

**[Risk] LLM fills rationale with low-quality boilerplate** → The field
description in the schema should be specific: "State your intent for this
tool call in one sentence — what are you trying to accomplish and why?" Poor
rationale is still better than no rationale for compaction purposes. Quality
can be improved via system prompt guidance without code changes.

**[Risk] Token cost of meta fields in tool schemas** → Each tool definition
gains ~60 tokens for the three meta properties. With 20 tools in the system
prompt, that's ~1200 tokens. Acceptable relative to a 200K context window.
The per-call rationale cost (~20-50 tokens) scales with tool call volume but
is modest.

**[Risk] Meta field name collision with MCP tool parameters** → The underscore
prefix convention (`_rationale`, `_timeout_seconds`, `_background`) reduces
collision risk. If a collision occurs, the extraction logic should prefer the
meta interpretation (strip from arguments). MCP tool authors using underscore-
prefixed parameters is unlikely but would cause the parameter to be silently
consumed by the meta pipeline. Mitigation: log a warning if a stripped meta
field was also present in the original tool schema.

**[Risk] Protobuf schema versioning** → `MetaJson` is additive
(`ProtoMember(4)`) on `SerializableToolCall`. Old journals missing this field
deserialize to `null`. No migration needed. Forward-compatible because new
meta fields are added to the JSON, not the protobuf.

**[Trade-off] Flat fields vs nested `_meta` object** → Flat fields are more
reliably filled by LLMs but don't namespace cleanly. If we later need many
meta fields, we may want to migrate to a nested structure. The extraction
logic should be written to handle both patterns for forward compatibility.
