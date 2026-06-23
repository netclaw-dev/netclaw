# Proposal: Loud Tool-Argument Validation

## Why

Netclaw silently discards or degrades LLM-supplied tool arguments: unknown keys are
dropped without signal, present-but-unparseable values coerce to defaults, and
requested values (timeouts) are clamped or ignored with no notice in the tool result.
In production session `D0AC6CKBK5K_1781115410_840529`, the agent passed
`"TimeoutSeconds":"1200"` (instead of the recognized `_timeout_seconds`), the key was
silently dropped, the shell timeout fell back to a 90s default, and the agent's call
to its code delegate was killed mid-flight — the agent formed a false belief ("I set a
generous timeout") that fed a multi-hour stuck loop. A follow-up audit
(`SILENT_FALLBACK_AUDIT.md`) found 17 sites sharing three mechanisms. This violates
the constitution's no-silent-fallbacks rule and the secure-by-default posture of
PRD-001 / PRD-002: the config surface already enforces strict validation
(`additionalProperties: false` + `ConfigSchemaDoctorCheck`), but the tool-call
argument surface — the agent's highest-frequency input path — has no equivalent.

## What Changes

- **Unknown-key rejection (M1).** A tool call carrying an argument key that matches
  neither the tool's declared parameters nor the meta keys (`_rationale`,
  `_timeout_seconds`, `_background`) is rejected with a recoverable tool-result error
  before execution. The error names the unrecognized key and, when a near-miss is
  detected, includes a "did you mean `<canonical>`?" suggestion. **Fuzzy matching is
  used ONLY to generate the suggestion text — never to accept a near-miss key.**
  (Decided: no alias acceptance; system-side intent-guessing on a surface carrying
  timeout/background/path semantics has unacceptable blast radius. The LLM resolves
  the ambiguity explicitly by re-issuing.)
- **Present-but-invalid value rejection (M2).** A value that is present but
  unparseable for its declared type (e.g. `"abc"` for an int parameter,
  `_timeout_seconds: "1200ms"`, `_background: "yes"`) returns a tool-result error
  naming the parameter, the supplied value, and the expected type — instead of
  silently coercing to `0`/`false`/null. Absent optional parameters keep their
  documented defaults (no intent expressed → no error).
- **Malformed tool-call JSON surfaces (M2, provider boundary).** A tool call whose
  arguments JSON fails to deserialize produces a tool-result error for that call id
  instead of dispatching the call with null arguments.
- **Override notices (M3).** Every silent override of an agent-expressed value emits
  a model-facing notice appended to the tool result, reusing the existing
  `ToolOutputSpill.Compose` / `AppendModelInputHandoffWarning` notice patterns:
  - timeout hint clamped to `MaxToolTimeoutSeconds` ceiling → `[timeout clamped from
    1200s to 600s maximum; use _background:true for longer work]`
  - timeout hint below the tool default floor → noted, not silently ignored
  - `web_fetch` `Format` value outside `{raw, text}` → error (not silent raw fallback)
  - `web_fetch` 5 MB response cap reached → truncation marker in the summary
- **Phantom argument fix.** `list_webhooks`' schema-advertised `Filter` parameter is
  currently never read (complete no-op); it will be honored (filter on
  `definition.Enabled`) with the applied filter echoed in the result.
- **Skill guidance.** `netclaw-operations` system skill updated: long-running
  delegation calls (e.g. HTTP calls to a local coding-agent server) must use
  `_background: true` rather than a synchronous shell call under the timeout ceiling.

Not breaking for well-formed callers: tool calls using declared parameters and valid
values behave identically. Calls that previously "succeeded" by silently dropping
arguments will now error — that is the intended behavior change.

## Capabilities

### New Capabilities

- `tool-arg-validation`: validation contract for LLM-supplied tool arguments at the
  dispatch seam — unknown-key rejection with suggestion-only near-miss matching,
  present-but-invalid value rejection, absent-vs-invalid distinction, malformed
  args-JSON handling at the provider boundary, and the model-facing override-notice
  mechanism.

### Modified Capabilities

- `tool-call-metadata`: the "Per-call timeout hint" requirement currently specifies
  *silent* clamping to the ceiling and *silent* ignoring of below-floor hints
  (scenarios "Timeout hint exceeds ceiling", "Timeout hint below tool default
  ignored"). Both change: the effective value still clamps/floors, but the override
  is surfaced in the tool result. Malformed meta values (`_timeout_seconds`,
  `_background`) change from silent drop to a tool-result error.
- `netclaw-tools`: `web_fetch` gains explicit `Format` validation and a truncation
  marker at the response-byte cap; `list_webhooks` gains honored `Filter` semantics.

## Impact

- **Affected code:**
  - `Netclaw.Tools.Generators/NetclawToolGenerator.cs` (generated `ParseArguments` —
    unknown-key diff + invalid-value errors; this is the seam covering all ~20 native
    tools)
  - `Netclaw.Tools.Abstractions/ToolCallMeta.cs`, `ToolArgumentHelper.cs` (meta
    extraction, absent-vs-invalid distinction; also fix the latent uncaught-throw on
    non-integral JSON numbers via `TryGetInt32`)
  - `Netclaw.Actors/Sessions/Pipelines/ToolCallMetaExtractor.cs`,
    `SessionToolExecutionPipeline.cs` (clamp/floor notices on the result path)
  - `Netclaw.Providers/SelfHosted/OpenAiCompatibleChatClient.cs`
    (`TryDeserializeArguments` null-args dispatch)
  - `Netclaw.Actors/Tools/WebFetchTool.cs`, `ListWebhooksTool.cs`
  - `feeds/skills/.system/files/netclaw-operations/SKILL.md` (+ version bump, per the
    System Skills Sync Rule)
- **Reused constructs (no new parallel mechanisms):** near-miss suggestion modeled on
  `ApprovalPatternMatching`'s `ApprovalNearMiss` shape; notices via the existing
  result-append patterns; key normalization via `ToolArgumentHelper.NormalizeKey`
  (for suggestion generation only).
- **MCP tools:** unchanged — MCP servers validate their own schemas and reject
  observably through `McpToolAdapter`'s existing error surface (`mcp-schema-coercion`
  remains authoritative).
- **Tests/evals:** tool-definition behavior changes → eval suite run required per the
  constitution's Eval Suite rule; new unit coverage for the validation seam.

### Security and Operational Impact

- **Security:** net positive — closes a class the constitution flags as
  privilege-escalation-adjacent (silently altered execution semantics). No policy or
  ACL decision changes in this change. Rejection happens *before* execution, so no
  partial side effects. The validator is fail-closed: ambiguity → error, never guess.
- **Operational:** a transient rise in tool-call errors is expected immediately after
  deployment as resident models learn canonical keys from the error messages; errors
  are self-describing and recoverable in one round-trip, so no operator action is
  required. Override notices add small, bounded text to tool results.

### In Scope (MVP) vs Out of Scope

**In scope:** the four mechanisms above (M1, M2, M3, skill guidance) plus the
`list_webhooks` phantom-arg fix.

**Out of scope — parked as open questions for a security owner** (from
`SILENT_FALLBACK_AUDIT.md`, policy layer):

1. Audience allowlist non-authority: `ToolAudienceProfileResolver.IsProfileManagedTool`
   silently exempts non-managed tools (memory tools, `search_tools`, `load_tool`,
   `spawn_agent`, `check_background_job`) from `Allowlist` profiles — needs a
   product decision (govern vs document).
2. Non-interactive shell trust-zone enforcement skips unnormalizable path tokens
   (`continue`) while the working-directory branch fails closed — inconsistency
   should be resolved fail-closed.
3. Safe-verb auto-allow short-circuit emits no audit line — observability decision.

Also out of scope: stuck-loop / no-progress detection for agent sessions (separate
investigation, same originating incident); text-format tool-call parser type fidelity
(`TextToolCallParser` string flattening — borderline, needs its own design).

**Traceability:** PRD-001 (MVP tool surface), PRD-002 (gateway security envelope /
fail-closed posture); constitution "No silent fallbacks" quality bar; origin incident
documented in memorizer memory `e9a72b27-72d9-4e98-aad7-4d970ce52ecf`.
