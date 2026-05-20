## Why

netclaw coerces MCP tool-call arguments by guessing each value's type from its
runtime shape (`McpSchemaSanitizer.CoerceArguments` / `CoerceStringValue`),
never consulting the tool's declared `inputSchema`. This shape-driven approach
silently mutates argument values as they cross the netclaw→MCP boundary, and it
can neither detect nor repair a mismatch between what the model emitted and what
the tool declares. Two defects trace to it: issue #1093 — a model-double-encoded
array-of-objects argument is forwarded to the server as a string and rejected
(`MCP error -32602`); and a schema-blind corruption where a `string`-typed
argument such as `"00713"` is silently converted to the integer `713`. PR #1094
patched #1093 with a *second*, separate schema-aware normalizer
(`McpArgumentNormalizer`) bolted on downstream of the still-schema-blind coercer
— leaving two argument-massaging utilities, one schema-aware and one
schema-blind, three lines apart. This change makes the declared schema the
single authority for argument coercion.

## What Changes

- Make `McpSchemaSanitizer.CoerceArguments` **schema-driven**: it accepts the
  tool's declared `inputSchema` and coerces each argument *toward* its declared
  type rather than guessing from value shape.
- Schema-directed coercion rules: reconstruct stringified `array`/`object`
  values into structured form; leave `string`-typed parameters untouched (no
  number/bool guessing); coerce string→`integer`/`number`/`boolean` only when
  the schema declares those; pass values through unchanged when the schema is
  absent or does not constrain the type.
- Handle both representations a stringified container can arrive in — a
  `System.String` and a `JsonElement` of `ValueKind.String` (the actual shape of
  `FunctionCallContent.Arguments` values).
- **Remove** `McpArgumentNormalizer` and `McpArgumentNormalizerTests` (added in
  PR #1094) and the `McpClientManager.InvokeFunctionAsync` normalization hook.
  Their behavior is fully subsumed by schema-driven `CoerceArguments`, which
  already runs on every MCP invocation path.
- No public API surface change, no configuration schema change, no behavior
  change for correctly-shaped arguments — not a breaking change.

## Capabilities

### New Capabilities

- `mcp-schema-coercion`: how netclaw reconciles LLM-emitted tool-call argument
  values against an MCP tool's declared input schema before dispatch — which
  values are coerced, toward what type, and which are left untouched.

### Modified Capabilities

_None._ Argument coercion is not described by any existing spec — `netclaw-mcp`
covers tool-grant enforcement and `netclaw-tools` covers policy-gated
invocation. This change documents previously-unspecified behavior, so it is
captured as a new capability rather than a delta against an existing spec.

## Impact

- **Code (modified):** `src/Netclaw.Actors/Tools/McpSchemaSanitizer.cs`
  (`CoerceArguments` signature + coercion logic);
  `src/Netclaw.Actors/Tools/McpToolAdapter.cs` (thread the declared schema into
  `CoerceArguments` on all three invocation paths — `ExecuteAsync` via the
  invoker, `ExecuteViaBoundToolAsync`, and `SanitizedAIFunction.InvokeCoreAsync`).
- **Code (removed):** `src/Netclaw.Daemon/Mcp/McpArgumentNormalizer.cs`,
  `src/Netclaw.Daemon.Tests/Mcp/McpArgumentNormalizerTests.cs`, and the
  normalization hook in `src/Netclaw.Daemon/Mcp/McpClientManager.cs`.
- **PRD:** PRD-006 MCP Tool Integration (MCP-006 Tool Discovery and
  Registration / tool calling pipeline). No dedicated PRD requirement exists for
  argument coercion; this is a correctness fix under that umbrella.
- **Tests:** new unit coverage for schema-driven `CoerceArguments` covering
  #1093 (stringified array-of-objects), the `"00713"`-class corruption, and the
  `JsonElement`-of-`ValueKind.String` representation that PR #1094's tests did
  not exercise.
- **Security & operational impact:** tool-call argument values cross the
  netclaw→MCP trust boundary. Shape-driven coercion is a *silent-mutation* path
  — it can change a value's type or content without the schema's authority
  (e.g. dropping leading zeros from an identifier, or escalating a `"true"`
  string to a boolean). Schema-directed coercion removes that path: every
  coercion is justified by the declared type, aligning with the constitution's
  "no silent fallbacks" and "no implicit conversions" rules. This is primarily a
  data-integrity/correctness concern; there is no change to ACL or grant
  evaluation. No runbook, CLI, or configuration-surface impact.
- **Out of scope:** smoke MCP server enrichment plus a deterministic
  argument-fidelity harness — a separate follow-up PR.
