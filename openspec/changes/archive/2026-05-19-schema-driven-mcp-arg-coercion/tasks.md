## 1. Schema-directed coercion core (`McpSchemaSanitizer`)

- [x] 1.1 Add a declared-type resolver that reads `properties.<name>.type` from a tool input schema and returns the set of permitted JSON value kinds; treat a missing property, an empty `{}` property schema, a `$ref`/`anyOf`/`oneOf`/`allOf`-only schema, or an unrecognized `type` value as "undeclared".
- [x] 1.2 Resolve union `type` arrays (e.g. `["array","null"]`) to the set of non-null kinds.
- [x] 1.3 Change `CoerceArguments` to `CoerceArguments(IDictionary<string,object?>? arguments, JsonElement schema)`; treat a `schema` whose `ValueKind` is not `Object` as "no usable schema" (pass-through).
- [x] 1.4 Implement schema-directed per-argument coercion: reconstruct a stringified `array`/`object` value — from a `System.String` or a `JsonElement` of `ValueKind.String` — only when it parses as a declared kind; coerce a string to `integer`/`number`/`boolean` only when the schema declares that scalar; leave `string`-declared and undeclared-type parameters unchanged; pass already-structured values through as `JsonElement`.
- [x] 1.5 Remove the `JsonElement`→`List`/`Dictionary` deep conversion (`ConvertJsonValue` recursion) — structured values pass through unchanged, not rewritten into CLR trees.
- [x] 1.6 Parse numeric strings with invariant culture (JSON numeric format), not the current culture, in the surviving scalar-coercion path.
- [x] 1.7 Ensure coercion never throws — a JSON parse failure yields pass-through, not an exception.

## 2. Wire the declared schema through `McpToolAdapter`

- [x] 2.1 Capture the raw `McpClientTool` input schema (`_mcpTool` as `AIFunction` → `JsonSchema`) at `McpToolAdapter` construction.
- [x] 2.2 Pass the raw schema to `CoerceArguments` on all three dispatch paths — `ExecuteAsync`, `ExecuteViaBoundToolAsync`, and `SanitizedAIFunction.InvokeCoreAsync` — preserving the `StripMetaFields → NormalizeArgumentKeys → CoerceArguments` ordering.
- [x] 2.3 Log a warning when the adapter has no usable input schema for a tool (the defensive guard from design.md).

## 3. Remove the duplicate normalizer

- [x] 3.1 Remove the `McpArgumentNormalizer.Normalize` hook from `McpClientManager.InvokeFunctionAsync`, restoring it to a pure transport with no argument shaping.
- [x] 3.2 Delete `src/Netclaw.Daemon/Mcp/McpArgumentNormalizer.cs`.
- [x] 3.3 Delete `src/Netclaw.Daemon.Tests/Mcp/McpArgumentNormalizerTests.cs`.
- [x] 3.4 Confirm `Netclaw.Daemon` and `Netclaw.Daemon.Tests` build with no remaining references to `McpArgumentNormalizer`.

## 4. Tests

- [x] 4.1 Rewrite `McpToolAdapterTests.CoerceArguments_*` to pass a schema and assert schema-directed behavior (the old `CoerceArguments_ConvertsStringNumbers` becomes "coerces a numeric string under an `integer` schema").
- [x] 4.2 Add `CoerceArguments` tests for #1093: an array-of-objects argument as a `System.String`, and as a `JsonElement` of `ValueKind.String`, is reconstructed into a structured JSON array under an `array` schema.
- [x] 4.3 Add tests for the corruption class: a `string`-declared parameter preserves `"00713"` (leading zeros, string type) and `"true"` unchanged.
- [x] 4.4 Add scalar-gating tests: `"42"` coerced under an `integer` schema; `"42"` left unchanged when the schema declares no scalar type.
- [x] 4.5 Add pass-through tests: parsed-kind mismatch (string parses as object, schema says `array`), unparseable string, already-structured value, union `["array","null"]`, undeclared type (`{}` and `anyOf`), and no usable schema.
- [x] 4.6 Add a test that `CoerceArguments` does not mutate its input dictionary (returns a distinct instance) — coercion cannot retroactively affect argument values an authorization or approval decision already evaluated.

## 5. Verification & quality gates

- [x] 5.1 Build the solution; run `Netclaw.Actors.Tests` and `Netclaw.Daemon.Tests` — all green.
- [x] 5.2 Run the existing MCP stdio smoke tests (`McpStdioSmokeTests`) — confirm no regression on the `add`/`echo` tools.
- [x] 5.3 `dotnet slopwatch analyze` — no new violations.
- [x] 5.4 `./scripts/Add-FileHeaders.ps1 -Verify` — copyright headers present, accounting for new and deleted files.

## 6. Docs & spec sync

- [x] 6.1 Confirm no PRD or `docs/spec/` update is required — PRD-006 (MCP-006) is unchanged; the OpenSpec `mcp-schema-coercion` capability is the spec record for this behavior.
- [x] 6.2 Check whether the `netclaw-operations` system skill describes MCP tool-argument handling; update it only if it does — no agent-facing behavior change is expected.
- [x] 6.3 On completion, run `/opsx-verify` then `/opsx-sync` to fold the `mcp-schema-coercion` delta into `openspec/specs/`.
