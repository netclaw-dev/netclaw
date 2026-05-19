## Context

MCP tool-call arguments are shaped in two places today, and only one of them
consults the tool schema:

- `McpSchemaSanitizer.CoerceArguments` (in `Netclaw.Actors.Tools`) — invoked by
  `McpToolAdapter` on all three MCP dispatch paths. It coerces values by
  guessing from runtime shape: `CoerceStringValue` turns any numeric- or
  boolean-looking string into a number/bool, `ConvertJsonValue` recurses
  `JsonElement` trees into CLR `List`/`Dictionary`. It never reads the schema.
- `McpArgumentNormalizer` (in `Netclaw.Daemon.Mcp`) — added by PR #1094, invoked
  by `McpClientManager.InvokeFunctionAsync`, three layers downstream. It *is*
  schema-aware: it reconstructs stringified `array`/`object` values.

So one schema-blind coercer and one schema-aware normalizer sit a few call
frames apart, and #1094's normalizer only works because `CoerceArguments` runs
first and happens to turn the `JsonElement` of `ValueKind.String` it cannot see
into a `System.String` it can. Issue #1093 (an array-of-objects argument the
model double-encoded as a string is dispatched as a string and rejected) and
the schema-blind corruption (`"00713"` → `713` on a `string` parameter) both
trace to the schema-blind coercer.

Coercion runs inside `McpToolAdapter.ExecuteAsync`, on the tool-execution
pipeline's thread-pool path — off the session actor's thread.
`McpSchemaSanitizer` is static and stateless. Arguments are persisted (as
`SerializableToolCall.ArgumentsJson`) in their raw, model-emitted form *before*
coercion; coercion is a dispatch-time transform and is never persisted.

## Goals / Non-Goals

**Goals:**

- One schema-driven argument-coercion path, owned by `McpSchemaSanitizer` and
  fed by `McpToolAdapter`, which already holds the schema.
- The MCP tool's declared input schema is the sole authority for coercion.
- All three MCP dispatch paths are covered identically — no path forwards raw
  arguments.
- `McpArgumentNormalizer` and the `McpClientManager` hook are deleted; the
  daemon's `McpClientManager` returns to being a pure transport with no
  argument shaping.

**Non-Goals:**

- Recursive, schema-directed coercion *into* array items and nested object
  properties. This change coerces top-level parameters only (see Open
  Questions).
- General JSON Schema validation. We read declared `type` only; we do not
  enforce `enum`, `required`, ranges, or `pattern`.
- Schema *sanitization* for LLM grammar compatibility (`SanitizeSchema`,
  `InjectMetaProperties`) — unchanged, separate concern.
- Smoke MCP server enrichment and a deterministic argument-fidelity harness —
  separate follow-up PR.

## Decisions

### Decision: `CoerceArguments` takes the declared schema

Change the signature to `CoerceArguments(IDictionary<string,object?>? arguments,
JsonElement schema)`. `JsonElement`'s default value is `ValueKind.Undefined`,
which serves as the natural "no schema" sentinel — when `schema` is not a JSON
object, every argument passes through unchanged.

*Alternative considered:* keep `CoerceArguments` schema-less and leave the
schema-aware step as a separate function. Rejected — that is the current
two-utilities split; consolidation is the point.

### Decision: coerce against the raw `McpClientTool.JsonSchema`, not the sanitized schema

`McpToolAdapter` holds both the raw tool schema (`_mcpTool` as `AIFunction`) and
the sanitized `ParameterSchema` (nullable unions collapsed, meta fields
injected, grammar-hostile keywords stripped). Coercion uses the **raw** schema:
it is the server's actual contract — the authority the spec names — whereas the
sanitized schema is a netclaw-internal derivative built for LLM grammar
compatibility. Coercing against a derivative risks reconciling arguments with
netclaw's transformation rather than the server's truth.

Consequence: the coercer must resolve a JSON Schema `type` that may be a string
(`"array"`) or a union array (`["array","null"]`). It resolves a parameter's
declared type into the set of JSON value kinds the schema permits.

### Decision: structured values pass through; stop the `JsonElement` → CLR tree rewrite

Today `ConvertJsonValue` deep-converts `JsonElement` arrays/objects into
`List<object?>`/`Dictionary<string,object?>`. No downstream consumer needs that
— the value flows into `AIFunctionArguments` and is JSON-serialized by the MCP
client, which serializes `JsonElement` identically. A correctly-emitted
`JsonElement` array now passes through whole. This both simplifies the coercer
and removes the recursion that would otherwise carry the schema-blind bug into
nested values.

### Decision: schema-directed coercion rules (top-level parameters)

For each argument, resolve the declared kinds from the schema, then:

- declared kinds include `array`/`object`, value is a string (`System.String`
  *or* `JsonElement` of `ValueKind.String`) that parses as one of those kinds →
  reconstruct to the structured `JsonElement`.
- declared kind is `string` → pass through unchanged; no numeric/boolean
  inference.
- declared kind is `integer`/`number`/`boolean`, value is a string → coerce
  toward *that* kind only (`CoerceStringValue` survives, but gated and
  directed).
- value already matches a declared kind → pass through unchanged.
- the parameter's type is undeclared — its property schema is `{}`, it is typed
  only via `$ref`/`anyOf`/`oneOf`/`allOf`, or it is absent from `properties` →
  pass through unchanged. This is the common, legitimate "any value" case.
- the tool exposes no usable schema at all → pass through unchanged, and log a
  warning (defensive guard; see the decision below).
- a value that fails to parse, or parses as a kind the schema does not declare
  → pass through unchanged. The MCP server rejects it explicitly with
  `MCP error -32602` — a loud failure one hop downstream, never hidden.

Coercion runs *after* `StripMetaFields` and `NormalizeArgumentKeys`, so it sees
canonical, meta-free keys that match the schema's `properties`. This ordering is
preserved on all three call sites.

### Decision: undeclared-type pass-through is fidelity, not a silent fallback

The constitution forbids silent fallbacks — degrading to a default when
something fails or is misconfigured. Passing an argument through uncoerced when
its parameter has no declared type is neither:

- **Nothing failed.** An empty property schema (`{}`) is valid JSON Schema
  meaning "any value"; a server declaring it is explicitly accepting any JSON
  there. Forwarding the model's value unchanged is the *faithful* action —
  coercion is the optional transform, and declining to transform an
  unconstrained value is the null operation, not a substituted default.
- **Nothing is hidden.** netclaw is never the last line of defense: the MCP
  server validates every call against its own schema and rejects bad input with
  `MCP error -32602`, surfaced back to the model as a tool error. A genuinely
  wrong value still fails loudly — one hop downstream, at the server.

The actual silent fallback was the *old* behavior — schema-blind
`CoerceStringValue` guessing a type and mutating the value (`"00713"` → `713`).
That hid the mismatch. Schema-directed coercion removes the guess; it does not
add a fallback.

The one branch that genuinely brushes the rule is the defensive guard — netclaw
holding no usable schema at all for an MCP tool. The MCP protocol mandates
`inputSchema`, so this is a should-never-happen. netclaw passes through rather
than abort — fabricating a schema is impossible, aborting every call on a
missing schema is a worse failure mode than forwarding literal arguments the
server will still validate, and `-32602` remains the loud backstop — but it
logs a warning so the anomaly is visible without killing the call.

### Decision: delete `McpArgumentNormalizer`, restore `McpClientManager` as pure transport

Remove `McpArgumentNormalizer.cs`, `McpArgumentNormalizerTests.cs`, and the
hook in `McpClientManager.InvokeFunctionAsync`. After this, all argument shaping
lives in the `Netclaw.Actors` layer at `McpToolAdapter` — the single place that
holds the schema — and the daemon's `McpClientManager` performs no argument
transformation.

## Risks / Trade-offs

- **Removing nested schema-blind coercion may regress nested scalar-as-string
  inputs** (e.g. an Ollama-style `{ items: ["1","2"] }` where `items` are
  declared `integer`). → *Mitigation:* #1093 and the observed corruption are
  both top-level; the removed nested coercion was itself schema-blind and
  carried the `"00713"` bug. If a real nested case appears, the correct fix is
  recursive coercion *with* the nested `items`/`properties` schema — tracked as
  an open question, not a silent reintroduction of shape-guessing.
- **A parameter typed via `anyOf`/`oneOf` instead of `type` reads as
  "no declared type"** and passes through uncoerced. → *Mitigation:* acceptable
  and conservative — pass-through is the safe default; the server still
  validates. Documented behavior, not a silent gap.
- **Coercion is post-authorization.** If any approval re-check were to run on
  coerced arguments, a coerced value could differ from what the model emitted. →
  *Mitigation:* coercion runs strictly inside `McpToolAdapter.ExecuteAsync`,
  after `ToolAccessPolicy` authorization, which evaluates raw `tc.Arguments`;
  spec requirement "Coercion does not bypass authorization" locks this, and a
  test asserts it.
- **Behavior change for existing tests.** `McpToolAdapterTests.CoerceArguments_*`
  assert the old schema-blind behavior and must be rewritten against schemas. →
  *Mitigation:* covered in tasks; the new tests feed the real
  `JsonElement{String}` shape that #1094's tests missed.

## Migration Plan

No data, configuration, or persisted-format migration. `ArgumentsJson` is still
persisted raw; coercion remains a dispatch-time transform, so a cold-recovered
session re-coerces deterministically from raw arguments. Deploy is a daemon
release. Rollback is a straight revert — the `CoerceArguments` signature change
and `McpArgumentNormalizer` removal are internal, with no external surface and
no schema change. No feature flag.

## Open Questions

1. **Recursive coercion** — do self-hosted/Ollama users emit nested
   scalars-as-strings often enough to need schema-directed recursion into array
   items and object properties now, or is top-level sufficient for this change
   with recursion as a fast follow?

(The earlier question on `anyOf`/`oneOf`-typed parameters is resolved: they read
as undeclared-type and pass through — see the coercion-rules and
fidelity-not-fallback decisions above.)
