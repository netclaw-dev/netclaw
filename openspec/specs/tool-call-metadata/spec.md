# tool-call-metadata Specification

## Purpose

Define a cross-cutting metadata envelope (`ToolCallMeta`) that is injected into
every tool's JSON schema and populated by the LLM as part of normal tool
calling. The metadata captures the model's intent (`_rationale`), a per-call
synchronous timeout hint (`_timeout_seconds`), and an explicit background
execution signal (`_background`). The tool execution pipeline extracts these
fields before dispatch so tool implementations never receive them, honors the
timeout hint as requested (rejecting only invalid values), persists the
metadata on the tool call for journal replay, and enriches audit entries with
the rationale and timeout hint. This capability defines the signaling and metadata mechanism only;
actual background job execution is consumed by a follow-on change.

## Requirements

### Requirement: ToolCallMeta envelope on every tool invocation

The system SHALL inject cross-cutting metadata fields into every tool's JSON
schema before presenting tool definitions to the LLM. The injected fields
SHALL use an underscore prefix convention (`_rationale`, `_timeout_seconds`,
`_background`) to avoid collision with tool-defined parameters. The LLM SHALL
populate these fields as part of normal tool calling. The tool execution
pipeline SHALL extract meta fields from the argument dictionary before
dispatching to the tool implementation. Tool implementations SHALL NOT receive
meta fields in their arguments.

#### Scenario: Meta fields present in tool schema

- **WHEN** the system builds tool definitions for the LLM
- **THEN** every tool schema includes `_rationale` (required string),
  `_timeout_seconds` (optional integer), and `_background` (optional boolean)
- **AND** the field descriptions guide the LLM to provide meaningful values

#### Scenario: Meta fields extracted before tool dispatch

- **GIVEN** the LLM issues a tool call with `_rationale`, `_timeout_seconds`,
  and tool-specific arguments
- **WHEN** the pipeline processes the tool call
- **THEN** `_rationale`, `_timeout_seconds`, and `_background` are extracted
  from the arguments
- **AND** the tool receives only its own defined parameters
- **AND** the extracted meta is stored as a `ToolCallMeta` object

#### Scenario: Native tool schema augmented at compile time

- **GIVEN** a tool is defined using `NetclawToolAttribute` and the source
  generator
- **WHEN** the source generator emits the tool's `inputSchema`
- **THEN** the schema includes the three meta properties appended after the
  tool's own parameters

#### Scenario: MCP tool schema augmented at runtime

- **GIVEN** an MCP server provides a tool with its own schema
- **WHEN** `McpToolAdapter` sanitizes the schema
- **THEN** the three meta properties are injected into the schema
- **AND** before forwarding arguments to the MCP server, meta fields are
  stripped from the argument dictionary

#### Scenario: Meta field collision with MCP tool parameter

- **GIVEN** an MCP tool defines a parameter named `_rationale`
- **WHEN** the pipeline extracts meta fields
- **THEN** the meta interpretation takes precedence (field is consumed as meta)
- **AND** a warning is logged indicating the collision

### Requirement: Rationale is required

The `_rationale` field SHALL be marked as required in every tool's JSON schema.
The field description SHALL instruct the LLM: "State your intent for this tool
call in one sentence — what are you trying to accomplish and why?"

#### Scenario: LLM provides rationale on tool call

- **GIVEN** the LLM issues a tool call
- **WHEN** the tool call includes a `_rationale` value
- **THEN** the rationale is extracted and stored on the `ToolCallMeta`

#### Scenario: Missing rationale does not block execution

- **GIVEN** the LLM issues a tool call without `_rationale` (model
  noncompliance)
- **WHEN** the pipeline extracts meta fields
- **THEN** the tool still executes (meta extraction is best-effort)
- **AND** the `ToolCallMeta.Rationale` is null

### Requirement: Per-call timeout hint

The `_timeout_seconds` field SHALL allow the LLM to set a per-call timeout. A
positive value SHALL be honored exactly — it SHALL NOT be clamped to a ceiling
nor floored to the tool default; the agent owns this judgement. When no hint is
provided, the inherited per-call default (`SessionConfig.ToolExecutionTimeout`)
SHALL apply. The pipeline SHALL use this value when creating the per-call
`CancellationTokenSource`, and the same value SHALL govern the background-job
path when `_background` is set. (A present-but-invalid value — non-positive or
unparseable — is rejected before dispatch; see "Malformed meta values".)

#### Scenario: Timeout hint is honored exactly

- **GIVEN** the LLM requests `_timeout_seconds: 1200` on a shell_execute call
- **WHEN** the pipeline creates the cancellation token
- **THEN** the timeout is set to 1200 seconds
- **AND** nothing is appended to the tool result

#### Scenario: A small timeout hint is honored, not floored

- **AND** the LLM requests `_timeout_seconds: 10`
- **WHEN** the pipeline creates the cancellation token
- **THEN** the timeout is set to 10 seconds (no floor is imposed)

#### Scenario: No timeout hint uses the inherited default

- **GIVEN** the LLM does not provide `_timeout_seconds`
- **WHEN** the pipeline creates the cancellation token
- **THEN** the `SessionConfig.ToolExecutionTimeout` default applies

#### Scenario: Background path honors the same hint

- **GIVEN** the LLM sets `_background: true` and `_timeout_seconds: 1800`
- **WHEN** the call is routed to a background job
- **THEN** the job's timeout is 1800 seconds (not clamped)

### Requirement: Background execution signal

The `_background` field SHALL allow the LLM to explicitly request background
execution. `_timeout_seconds` SHALL remain a synchronous timeout hint and SHALL
NOT automatically promote the call to background execution. Background
execution routing SHALL be consumed by a follow-on change (background job
execution); this spec defines only the signaling mechanism.

#### Scenario: Explicit background request

- **GIVEN** the LLM sets `_background: true` on a tool call
- **WHEN** the pipeline extracts meta fields
- **THEN** `ToolCallMeta.Background` is true
- **AND** the pipeline routes to background execution (when available)

#### Scenario: Timeout hint does not trigger background

- **GIVEN** the LLM requests `_timeout_seconds: 300`
- **AND** `_background` is absent or false
- **WHEN** the pipeline extracts meta fields
- **THEN** the timeout hint is applied only to synchronous execution
- **AND** the pipeline does not treat the call as a background execution
  request

#### Scenario: Background not yet implemented returns sync execution

- **GIVEN** background job execution is not yet available
- **AND** the LLM requests `_background: true`
- **WHEN** the pipeline processes the tool call
- **THEN** the tool executes synchronously with the requested timeout
- **AND** a log message indicates background execution was requested but is not
  yet available

### Requirement: Malformed meta values reject the call

The pipeline SHALL reject a tool call carrying a meta key whose value cannot
be parsed as its declared type (`_timeout_seconds` not a positive integer;
`_background` not a boolean) with a tool-result error before dispatch, naming
the meta key, the supplied value, and the expected type. The tool SHALL NOT
execute with default semantics in place of the expressed intent. Validation
state SHALL be computed pipeline-side; the persisted `ToolCallMeta` type SHALL
remain unchanged.

#### Scenario: Unparseable timeout value rejects instead of silently defaulting

- **GIVEN** a tool call with `"_timeout_seconds": "1200ms"`
- **WHEN** the pipeline extracts meta fields
- **THEN** the call is rejected with an error naming `_timeout_seconds`, the
  value `"1200ms"`, and the expected type positive integer
- **AND** the tool does not execute under the default timeout

#### Scenario: Non-boolean background value rejects

- **GIVEN** a tool call with `"_background": "yes"`
- **WHEN** the pipeline extracts meta fields
- **THEN** the call is rejected with an error naming `_background` and the
  expected type boolean
- **AND** the tool does not execute synchronously in place of the request

#### Scenario: Non-integral JSON number for timeout is handled, not thrown

- **GIVEN** a tool call with `"_timeout_seconds": 12.5`
- **WHEN** the pipeline extracts meta fields
- **THEN** extraction does not throw an uncaught exception
- **AND** the call is rejected as present-but-invalid

#### Scenario: Legacy persisted tool call re-drives deterministically

- **GIVEN** a persisted tool call carrying a malformed meta value is re-driven
  after recovery
- **WHEN** the pipeline extracts meta fields
- **THEN** the same rejection error is produced (deterministic on replay)

### Requirement: ToolCallMeta persistence

Extracted `ToolCallMeta` SHALL be persisted on `SerializableToolCall` as an
opaque JSON string field (`MetaJson`, protobuf tag 4). Existing persisted
messages without this field SHALL deserialize with null meta (backwards
compatible). The meta SHALL survive journal replay and snapshot recovery.

#### Scenario: Meta persisted on tool call

- **GIVEN** the LLM issues a tool call with rationale and timeout hint
- **WHEN** the tool call is persisted to the journal
- **THEN** `SerializableToolCall.MetaJson` contains the serialized
  `ToolCallMeta`
- **AND** the meta round-trips through protobuf serialization

#### Scenario: Legacy tool call without meta deserializes cleanly

- **GIVEN** a journal contains tool calls persisted before this change
- **WHEN** the session recovers from the journal
- **THEN** `SerializableToolCall.MetaJson` is null
- **AND** the session operates normally

### Requirement: Audit entry enrichment

`ToolAuditEntry` SHALL include `Rationale` and `TimeoutHintSeconds` fields
populated from the extracted `ToolCallMeta`. These fields SHALL be logged
alongside existing audit fields (session ID, tool name, call ID, timestamp,
allow/deny, duration, approval decision).

#### Scenario: Audit entry includes rationale

- **GIVEN** the LLM provides a rationale on a tool call
- **WHEN** the audit entry is logged
- **THEN** the entry includes the `Rationale` string

#### Scenario: Audit entry includes timeout hint

- **GIVEN** the LLM provides a timeout hint on a tool call
- **WHEN** the audit entry is logged
- **THEN** the entry includes the `TimeoutHintSeconds` value as requested by the LLM
