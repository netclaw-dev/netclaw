# Silent-Fallback / Silent-Discard Audit — Tool-Call Surface

**Origin:** session `D0AC6CKBK5K_1781115410_840529` (memory `e9a72b27`). The agent passed
`"TimeoutSeconds":"1200"` to extend a shell timeout; Netclaw only recognizes the meta key
`_timeout_seconds`, so the value was silently dropped, the timeout fell back to a 90s default,
and the agent got **no signal** — forming a false belief that fed a stuck loop.

**Constitution rule violated:** *"When something fails or is misconfigured, fail loudly — do
not silently degrade to a default… on security-relevant paths they can silently escalate
privileges."* The rule is enforced on the **config** surface (`additionalProperties:false` +
`ConfigSchemaDoctorCheck`) but **not** on the live **tool-call argument** surface. That
asymmetry is the gap.

**Method:** 3 parallel auditors (arg/meta/pipeline layer; tool implementations; policy/security
layer), identical rubric. CRITICAL finding independently re-verified by hand and down-graded.

**Resolution status (change `loud-tool-arg-validation`):** findings #1, #2, #4, #5, #6, #7,
#8, #9, #10 are **FIXED** by this change (unknown-key validator at the dispatcher,
strict value binding in the generator + `ToolArgumentHelper`, pipeline-side meta-value
rejection, `ComputeEffectiveTimeout` clamp/floor notices via `ToolExecutionContext.Notices`,
provider-boundary args-parse sentinel, `web_fetch` format validation + truncation notice,
`list_webhooks` Filter honored). The latent `GetInt32` uncaught-throw is also fixed
(`TryGetInt32`). Findings #3, #11, #15, #17 (policy layer) remain **OPEN — parked** for a
security owner, recorded as out-of-scope open questions in the change proposal. #12–#14, #16
(BORDERLINE) remain open, unchanged.

---

## The class unifies into 3 mechanisms + standalones

Most of the 17 sites are not independent bugs — they are three repeated shapes:

- **M1 — unknown / near-miss key silently dropped.** The original bug. Lives at *two* layers:
  generated `ParseArguments` (all tools) and `ToolCallMeta.ExtractFrom` (meta keys, exact-match).
- **M2 — present-but-invalid value silently coerced to a default.** `_ => null`/`_ => false`
  switch arms; malformed args JSON → null args.
- **M3 — requested value silently clamped/overridden.** timeout floor & ceiling; format
  fallback; output/byte truncation with no marker.

A loud-by-default tool-arg seam (reject unknown keys + "did you mean" + surface every
override) closes M1–M3 for all ~20 tools at ~3 chokepoints.

---

## Findings (severity-ranked, de-duplicated)

| # | file:line | mechanism | what's silently handled | verdict | sev | minimal loud fix |
|---|---|---|---|---|---|---|
| 1 | `Netclaw.Tools.Generators/NetclawToolGenerator.cs:234-296` + `DispatchingToolExecutor.cs:67` | M1 | **any** unknown/misspelled arg key, all tools (the exact `TimeoutSeconds` mechanism) | VIOLATION | HIGH | diff supplied keys vs schema props (+ meta keys); return `Error: unrecognized argument 'X'. Did you mean 'Y'?` |
| 2 | `Netclaw.Tools.Abstractions/ToolCallMeta.cs:69,80,95` | M1 | near-miss meta keys (`TimeoutSeconds`, `_timeoutSeconds`, `_timeout-seconds`) — exact-match `TryGetValue`, unlike normal args which use `ToolArgumentHelper.TryGetValueFlexible` | VIOLATION | HIGH | reuse the flexible matcher for meta keys; emit "did you mean `_timeout_seconds`?" notice on near-miss |
| 3 | `ToolAudienceProfileResolver.cs:70-71,175-192` | (policy) | audience `Allowlist` profile silently does **not** govern tools outside a hardcoded set (memory tools, `search_tools`, `load_tool`, `spawn_agent`, `check_background_job`) → always allowed | VIOLATION / maybe by-design | HIGH (re-classified from CRITICAL) | product decision: default-deny unmanaged first-party tools under Allowlist, **or** document + surface that the allowlist is non-authoritative. Approval gate still applies (mitigates). |
| 4 | `ToolArgumentHelper.cs:111,129,145` (`_ => null`) + generator `:260,274,288` (`?? 0/0.0/false`) | M2 | present-but-unparseable scalar (`"abc"` int, `"yes"` bool) → coerced to `0`/`false` | VIOLATION | HIGH | distinguish absent vs present-but-unparseable; throw/surface on the latter |
| 5 | `ToolCallMeta.cs:89,103` (`_ => null`/`_ => false`) | M2 | malformed `_timeout_seconds`/`_background` value → meta silently empty → default | VIOLATION | HIGH | surface "ignored `_timeout_seconds=\"1200ms\"` — expected positive int; used default" |
| 6 | `Netclaw.Providers/SelfHosted/OpenAiCompatibleChatClient.cs:779-792` (`TryDeserializeArguments`) | M2 | malformed/truncated tool-call args JSON → `return null`, call dispatched with **null args** | VIOLATION | HIGH | emit a tool-result error for that call id instead of an arg-less dispatch |
| 7 | `Sessions/Pipelines/ToolCallMetaExtractor.cs:37-39` & `:41` | M3 | requested timeout below floor → default; above ceiling → silent `Math.Min` clamp (the literal prod scenario) | VIOLATION | HIGH | append `[timeout clamped 1200s→600s max]` / `[requested 10s below 60s floor]` to result |
| 8 | `Tools/ListWebhooksTool.cs:31` | standalone | schema-advertised `Filter` arg is **never read** — `ListRouteFiles()` ignores it; complete no-op | VIOLATION | MEDIUM | honor filter (`definition.Enabled`) or reject unknown values; echo applied filter |
| 9 | `Tools/WebFetchTool.cs:111` | M3 | `Format` ≠ `"text"` (e.g. `"markdown"`, typo) → silent raw-HTML fallback | VIOLATION | MEDIUM | validate `Format ∈ {raw,text}`; error/notice otherwise |
| 10 | `Tools/WebFetchTool.cs:212-217` → `:107,149` | M3 | body > 5 MB cap → truncated, summary shows count but **no "truncated" marker** | VIOLATION | MEDIUM | append `[content truncated at 5 MB — N bytes not fetched]` |
| 11 | `ToolAccessPolicy.cs:204-205` (non-interactive shell trust-zone) | (policy) | path token that `NormalizePathToken` returns null for → `continue` (unchecked); working-dir branch fails closed — inconsistent, allow-leaning | BORDERLINE | MEDIUM | fail closed on null-normalized token (`shell_unresolvable_path_token`) |
| 12 | `Protocol/ChatMessageConverter.cs:94-97` | M2 | persisted media file missing at request-build → `log + continue`; attachment vanishes from LLM message (log-only, model blind) | BORDERLINE | MEDIUM | insert `[attachment unavailable: <name>]` placeholder into contents |
| 13 | `Providers/SelfHosted/TextToolCallParser.cs:40` | M2 | text/XML tool-call params all coerced to trimmed **string**; arrays/objects/types lost (the **Qwen text-format path** — relevant: orchestrator was Qwen) | BORDERLINE | MEDIUM | try `JsonDocument.Parse` per value; keep structured form; surface unreconcilable values |
| 14 | `OpenAiCompatibleChatClient.cs:749-752` | M2 | streamed tool call missing `function.name` → `?? string.Empty` → masquerades as "unknown tool", args lost | BORDERLINE | MEDIUM | treat nameless finished call as stream-assembly error (log + diagnostic) |
| 15 | `ToolAccessPolicy.cs:493-525` (`ResolveApprovalMode`) | (policy) | a future `/`-bearing matcher key would skip `McpServerDefaults` (latent, not currently reachable) | BORDERLINE | MEDIUM | guard: matcher keys must be first-party (no `/`) |
| 16 | `Tools/WebSearchTool.cs:40` / `Tools/FileReadTool.cs:84-85` | M3 | `MaxResults` clamp to 30 (documented); `StartLine`/`Limit` 0/neg treated as unspecified | BORDERLINE | LOW | optional `[capped at 30]` note; reject non-positive line numbers |
| 17 | `ToolAccessPolicy.cs:338-346` (safe-verb short-circuit) | (policy) | read-only-verb auto-ALLOW with no audit line (cf. `LogApprovalNearMisses` which does log) | BORDERLINE | LOW | emit info/audit log on safe-verb auto-grant |

**Separate defect class (not silent-fallback, flagged for awareness):**
`ToolCallMeta.cs:87` — `je.GetInt32()` inside a `when` guard **throws** (uncaught) on a
non-integral/overflow JSON number (`_timeout_seconds: 12.5` or `1e12`). `ExtractFrom` has no
try/catch. Use `TryGetInt32`.

---

## Reusable "loud" patterns already in-repo (reuse before adding)

- **`ToolOutputSpill.Compose` (`ToolOutputSpill.cs:108-115`)** — gold standard model-facing
  truncation notice: `[output truncated to X of Y; saved to <path> — read a slice…]`.
- **`SessionToolExecutionPipeline.AppendModelInputHandoffWarning` (`:924`)** — in-band,
  model-facing notice driven by a requested-vs-actual gap. Exact mechanism for M3 overrides.
- **`ToolArgumentHelper.TryGetValueFlexible` / `NormalizeKey` (`:17-68`)** — case/punct-insensitive
  matcher; reuse inside `ToolCallMeta.ExtractFrom` to fix M1 meta-key drops *and* detect near-misses.
- **`ApprovalNearMiss` facility (`ApprovalPatternMatching.cs:192-311`, logged via
  `ToolApprovalActor.LogApprovalNearMisses:163-185`)** — already classifies
  "expected-match-but-missed" with a `Describe()`. A `ToolArgNearMiss` modeled on it gives the
  "supplied X, recognized form is Y, here's why" surface for M1. Read-only diagnostics — safe.
- **`RedirectToken` throw-on-unknown-enum (`IToolApprovalMatcher.cs:447`)** — correct alternative
  to `_ => default` on a security-relevant enum map.
- **`ShellTool` cwd handling (`:118-133`)** and **`RouteToBackgroundJobAsync` trust-context guard
  (`:734-736`, `throw … "trust context cannot be defaulted"`)** — constitution-aligned exemplars.

---

## Proposed spec spine (for the OpenSpec change)

1. **No-silent-discard invariant at the tool-arg seam.** Unknown keys → loud, recoverable
   tool-result error with near-miss "did you mean" (reuse `ApprovalNearMiss` shape + flexible
   matcher). Covers M1 at `NetclawToolGenerator.ParseArguments` + `ToolCallMeta.ExtractFrom`.
2. **Absent vs present-but-invalid.** Present-but-unparseable values surface, never coerce to
   `0`/`false`/`null`-args. Covers M2.
3. **Surface every override to the agent.** Clamp/floor/format-fallback/truncation emit a
   model-facing note (reuse `ToolOutputSpill`/`AppendModelInputHandoffWarning`). Covers M3.
4. **Policy decisions, separately.** #3 (allowlist authority), #11 (null token fail-closed),
   #17 (audit auto-grants) need a security-owner decision, not just a notice.
5. **Ergonomic fix alongside the safety net:** accept obvious aliases (`TimeoutSeconds` →
   `_timeout_seconds`) so correct intent just works; the validator is the backstop.

**Highest single leverage:** items 1 + 2 (the M1 seam) — closes the original bug's entire
class in ~2 places.
