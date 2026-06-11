# Design: Loud Tool-Argument Validation

## Context

LLM-supplied tool arguments flow through three seams today:

1. **Provider boundary** — `OpenAiCompatibleChatClient.TryDeserializeArguments`
   parses the model's arguments JSON; on `JsonException` it returns null and the
   call is dispatched with **null arguments**.
2. **Pipeline meta extraction** — `ToolCallMetaExtractor.Extract` →
   `ToolCallMeta.ExtractFrom` pulls `_rationale` / `_timeout_seconds` /
   `_background` by **exact key match** and silently drops malformed values
   (`_ => null` / `_ => false`). `ComputeEffectiveTimeout` silently clamps to the
   ceiling and silently ignores below-floor hints.
3. **Tool binding** — generated `ParseArguments` (per-tool, from
   `NetclawToolGenerator`) reads only declared parameters via
   `ToolArgumentHelper.Get*` (which match **flexibly**: exact first, then
   case/punctuation-normalized via `NormalizeKey`). Unknown keys are never
   inspected; present-but-unparseable values coerce to `0`/`0.0`/`false` through
   `GetNullable* … ?? default`.

Two channels already exist that this design reuses rather than duplicates:

- **Exception → error result**: `SessionToolExecutionPipeline` catches any tool
  exception and converts it to `resultText = "Error executing tool: {message}"`
  (`:553-556`) — the generated `ParseArguments` already uses this for
  missing-required (`throw new ArgumentException`).
- **Post-bounding notice append**: `AppendModelInputHandoffWarning` (`:571-574`)
  appends model-facing notices to `resultText` *after* `ToolOutputSpill`
  bounding, so notices can never be windowed away.

Origin incident and full site inventory: `SILENT_FALLBACK_AUDIT.md` (repo root,
this branch); proposal.md for scope.

## Goals / Non-Goals

**Goals:**

- No LLM-supplied argument is ever silently discarded, coerced, or overridden:
  every such event either rejects the call with a self-describing, recoverable
  error (before execution) or surfaces a notice in the tool result.
- Rejection errors are actionable in one model round-trip ("did you mean
  `_timeout_seconds`?").
- Zero behavior change for well-formed calls.
- No new config knobs, no new persisted types, no new actors or messages.

**Non-Goals:**

- No fuzzy/alias *acceptance* of argument keys (explicitly decided against —
  see D2).
- No change to MCP tool argument handling (`mcp-schema-coercion` + server-side
  validation remain authoritative).
- No policy/ACL changes (parked in proposal).
- No stuck-loop/no-progress detection (separate workstream).
- No change to `TextToolCallParser` type fidelity (separate design needed).

## Decisions

### D1: Central unknown-key validation in `DispatchingToolExecutor`, driven by the tool's schema

Unknown-key checking runs once, centrally, in `DispatchingToolExecutor.ExecuteAsync`
before `tool.ExecuteAsync`, for **native tools only** (skip `McpToolAdapter`).
The recognized-key set is derived from the tool's existing `ParameterSchema`
(which the generator already augments with the meta keys), computed lazily once
per tool type and cached — no generator changes needed for the set itself.

**Recognition MUST mirror actual consumption semantics**, not an idealized rule:

- A supplied key is recognized iff it would actually be consumed downstream:
  - **declared parameters**: exact match OR `NormalizeKey`-equivalent — because
    `ToolArgumentHelper.TryGetValueFlexible` already binds flexibly today;
  - **meta keys** (`_`-prefixed): **exact match only** — because
    `ToolCallMeta.ExtractFrom` extracts exactly.
- Anything else → reject with a tool-result error, before execution.

If recognition were stricter than binding (exact-only for declared params), the
Qwen text-parser path — which emits lowercased keys that flexible binding
accepts today — would start failing on working calls. If it were looser than
extraction (flexible for meta keys), `TimeoutSeconds` would be "recognized" but
never consumed — recreating the original bug behind the validator.

*Alternatives considered:* (a) emit the check inside generated `ParseArguments`
— rejected: N generated copies of one rule, and the executor seam also covers
direct callers; (b) validate in `SessionToolExecutionPipeline` — rejected:
sub-agent and non-pipeline dispatch paths also funnel through
`DispatchingToolExecutor`, making it the true chokepoint.

### D2: Suggestions only — fuzzy matching never accepts

**Locked decision (user):** the system never acts on a guessed key. The
dividing line is *who resolves ambiguity*:

- **Deterministic canonicalization** (existing `NormalizeKey` case/punctuation
  folding for declared params) is retained — it is existing, deterministic
  consumption behavior, not guessing, and removing it would break working
  callers.
- **Guess-based matching** (edit distance, near-miss against meta keys) is used
  **only to generate the suggestion text** in the rejection error:
  `Unrecognized argument 'TimeoutSeconds'. Did you mean '_timeout_seconds'?
  The tool was NOT executed.` The LLM resolves the ambiguity by re-issuing
  explicitly.

Suggestion generation: `NormalizeKey`-equality against meta keys first (catches
the entire `TimeoutSeconds`/`_timeoutSeconds`/`timeout_seconds` family), then
edit-distance ≤ 2 against all recognized names. Modeled on the
`ApprovalNearMiss` shape (`ApprovalPatternMatching`): classify, describe,
never alter the decision. The error also lists the tool's valid argument names
(bounded — native tools have ≤ ~6 params + 3 meta keys).

### D3: Present-but-invalid values reject via strict helper variants

`ToolArgumentHelper` gains strict variants (`GetIntStrict`, `GetDoubleStrict`,
`GetBoolStrict`, and nullable counterparts) that distinguish three states:
**absent** (→ documented default, unchanged), **parsed** (→ value), and
**present-but-invalid** (→ `throw ArgumentException("Parameter 'Limit' value
'abc' is not a valid integer.")`). `NetclawToolGenerator` emits the strict
variants in `ParseArguments`; the existing `ArgumentException` → pipeline
catch → error-result channel surfaces it. Two latent value bugs are fixed in
the same pass: `double d => (int)d` silent truncation (12.7 → 12) becomes
invalid-unless-integral, and `JsonElement.GetInt32()` on non-integral/overflow
numbers (currently an **uncaught throw**) becomes `TryGetInt32` →
present-but-invalid.

The non-strict `GetNullable*` helpers remain for callers that legitimately
treat unparseable as absent (none known in generated code after this change;
audit flagged the `?? 0/0.0/false` arms specifically).

### D4: Malformed meta values reject the call — computed in the pipeline layer, not persisted

`ToolCallMeta.ExtractFrom` keeps its signature and the persisted `ToolCallMeta`
type is **unchanged** (it is persistence-owned; adding transient validation
state to it would leak pipeline concerns into the serialization contract).
Instead, `ToolCallMetaExtractor.Extract` (pipeline-side) returns validation
errors alongside the meta: a present-but-invalid `_timeout_seconds` or
`_background` value produces a tool-result error **before dispatch** — the
agent expressed execution semantics we cannot honor, so we do not run the call
on different semantics. Same rejection channel as D1/D3.

### D5: Override notices via a `Notices` list on `ToolExecutionContext`, appended post-bounding

`ToolExecutionContext` (already flowing through every seam — per the
constitution, reuse what is already at the call site) gains a
`List<string> Notices`. Producers:

- `SessionToolExecutionPipeline` / `ToolCallMetaExtractor.ComputeEffectiveTimeout`:
  ceiling clamp → `[timeout clamped: requested 1200s, maximum 600s — use
  _background:true for longer work]`; below-floor → `[timeout request 10s is
  below the 60s tool default; 60s applied]`.
- `WebFetchTool`: response-byte cap reached → `[content truncated at 5 MB — N
  bytes not fetched]`.

Notices are appended to `resultText` at the existing
`AppendModelInputHandoffWarning` seam — after `ToolOutputSpill` bounding, so a
notice can never be spilled or windowed away. Notices are additive text on the
existing result string: no persistence change (results are already persisted as
strings).

*Alternative considered:* return notices through tool return values — rejected:
changes the `string`-returning tool contract for every tool; the context object
already traverses the exact path needed.

### D6: Provider-boundary malformed args JSON → sentinel argument, rejected pre-dispatch

When `TryDeserializeArguments` fails, instead of dispatching null arguments,
the client attaches a single sentinel entry
`__netclaw_args_parse_error: "<JsonException message + first 200 chars of raw
payload>"`. The pipeline detects the sentinel before meta extraction and emits
a tool-result error for that call id without dispatching: `Tool call arguments
were not valid JSON: …. The tool was NOT executed.`

*Alternatives considered:* (a) custom `AIContent` subtype — rejected: invasive
across message conversion and persistence for one error path;
(b) drop the call silently — violates the invariant being established;
(c) keep null-args dispatch + detect downstream — rejected: the raw payload
(needed for a useful error) is only available at the client.
The sentinel never collides with validation: it is checked and consumed before
D1 runs, and if it ever leaked it is not in any schema → rejected loudly anyway.
On persistence re-drive the sentinel round-trips as an ordinary argument and the
pipeline rejects it again pre-dispatch — deterministic on replay.

### D7: In-tool fixes — `web_fetch` format, `list_webhooks` filter

- `WebFetchTool`: `Format` validated against `{null, "raw", "text"}`;
  anything else → `ArgumentException` (same channel as D3). The silent
  `useTextMode = Format == "text"` fallback is removed.
- `ListWebhooksTool`: `Filter` honored — `"active"` (default) filters on
  `definition.Enabled`, `"all"` returns everything, any other value rejects;
  the applied filter is echoed in the result header.

### D8: No escape hatches

No config knob disables validation (a toggle would be a sanctioned silent
fallback). If a future tool legitimately accepts free-form keys, it must opt
out explicitly in source (e.g. an `[AllowUnknownArguments]` attribute on the
tool class) where it is visible to review — not at runtime.

## Actor Boundaries and Persistence Implications

- **No new actors, messages, or protocols.** All changes live inside the
  session actor's existing tool-execution pipeline (`LlmSessionActor` →
  `SessionToolExecutionPipeline` → `DispatchingToolExecutor` → tool classes),
  which is already transport-agnostic. Sub-agent dispatch funnels through the
  same `DispatchingToolExecutor` and inherits validation unchanged.
- **No persisted-type changes.** `ToolCallMeta` / `SerializableToolCall` are
  untouched (D4 keeps validation state pipeline-side). Rejection errors and
  notices are ordinary tool-result strings, persisted through the existing
  `SerializableChatMessage` path. Legacy persisted tool calls deserialize and
  re-drive exactly as before; a re-driven call carrying a bad key is rejected
  deterministically (same input → same error), which is the correct replay
  semantic.

## Failure Modes and Recovery

- **Validator rejects a key a model insists on** → error is recoverable and
  self-describing (valid-key list + suggestion); the model corrects in one
  round-trip. If a model loops on the same rejection, that is the (separate)
  stuck-loop workstream's domain; the error text is deterministic, so loop
  detection sees identical failures — the easy case.
- **False rejection of a working call pattern** (biggest risk — e.g. a text-
  parser key shape we did not anticipate) → recognition mirrors binding
  semantics exactly (D1), and the eval suite + a replay of representative
  session logs gate the release. Recovery: revert; no data migration involved.
- **A tool with intentionally dynamic args breaks** → contingency is the
  explicit source-level opt-out (D8); audit during implementation confirms no
  current native tool needs it.
- **Notice text inflates context** → notices are single bounded lines, appended
  at most once per producer per call.
- **Sentinel arg persisted mid-rollout, processed post-rollback** → an unknown
  `__netclaw_args_parse_error` arg under old code is dropped by old binding
  (the old silent behavior) — degraded but not corrupt.

## Migration Plan

1. Land helpers + generator change + validator behind nothing (no flag — the
   change is the behavior).
2. Run `dotnet slopwatch analyze`, full test suite, eval suite (tool-definition
   change → required per constitution), and the light smoke tapes.
3. Release on the beta channel; watch session logs for `Unrecognized argument`
   / `not valid` error rates and any new tool-error loops.
4. Stable release after beta soak.
5. Rollback: revert the release tag; no persisted-schema or config migration in
   either direction.

## Open Questions

1. Should the rejection error enumerate valid keys always, or only when no
   near-miss suggestion is found? (Leaning: always — the list is small and
   removes a second failure round-trip.)
2. `skill_manage` / `set_webhook` accept structured sub-objects — confirm during
   implementation that their generated param surface is flat (expected) so the
   top-level key diff is sufficient; nested-object validation is out of scope.
3. Exact wording of the `_background` steer in the clamp notice — coordinate
   with the `netclaw-operations` skill update so the two use identical phrasing.
