# Tasks: loud-tool-arg-validation

## 1. Value-parsing helpers (foundation — no behavior change yet)

- [x] 1.1 Add strict variants to `ToolArgumentHelper` (`GetIntStrict`,
      `GetDoubleStrict`, `GetBoolStrict` + nullable counterparts) that
      distinguish absent / parsed / present-but-invalid; present-but-invalid
      throws `ArgumentException` naming parameter, supplied value, and expected
      type. Non-integral numeric for integer parameter is invalid (no `(int)d`
      truncation); replace `JsonElement.GetInt32()` with `TryGetInt32` so
      overflow/non-integral never throws uncaught.
- [x] 1.2 Unit tests for the strict helpers: absent → null/default, valid
      parses (int/long/string-number/JsonElement), invalid string, non-integral
      double, overflow JSON number, bool variants (`"yes"`, `1`, `"true"`).

## 2. Generator: bind with strict helpers

- [x] 2.1 Update `NetclawToolGenerator.ParseArguments` emission to call the
      strict variants for integer/number/boolean parameters (required,
      nullable, and optional-with-default arms) so present-but-invalid throws
      instead of coercing to `0`/`0.0`/`false`.
- [x] 2.2 Snapshot/golden tests for the generator output covering each
      parameter arm; verify a representative generated tool
      (e.g. `file_read` `Limit: "abc"`) surfaces
      `Error executing tool: Parameter 'Limit' value 'abc'…` through the
      pipeline catch.

## 3. Unknown-key validation in the dispatcher

- [x] 3.1 Compute and cache the recognized-key set per native tool in
      `DispatchingToolExecutor` (or `NetclawToolBase`): schema property names
      from `ParameterSchema` (includes meta keys); recognition = exact OR
      `NormalizeKey`-equal for declared params, exact-only for `_`-prefixed
      meta keys. Skip `McpToolAdapter`.
- [x] 3.2 Implement rejection: unrecognized key(s) → return tool-result error
      (do not execute) naming each key, stating the tool was NOT executed, and
      listing valid argument names.
- [x] 3.3 Implement suggestion generation (suggestion text ONLY — never
      acceptance): `NormalizeKey`-equality against meta keys first, then edit
      distance ≤ 2 against recognized names; model the near-miss
      classification on `ApprovalPatternMatching`'s `ApprovalNearMiss` shape.
- [x] 3.4 Unit tests: `TimeoutSeconds` → rejected with `_timeout_seconds`
      suggestion; `timeout_seconds` → rejected with suggestion (never bound);
      lowercase `command` → accepted (flexible binding preserved); exact
      `_timeout_seconds` → accepted; wholly unknown key → rejected without
      suggestion; MCP tool with extra key → not validated natively.
- [x] 3.5 Audit all native tools for intentionally free-form argument surfaces;
      if any exists, add the explicit source-level opt-out
      (`[AllowUnknownArguments]`) and a test proving it is honored — otherwise
      record "none needed" in the PR description.

## 4. Meta-value validation and override notices (pipeline)

- [x] 4.1 Extend `ToolCallMetaExtractor.Extract` (pipeline-side; persisted
      `ToolCallMeta` type unchanged) to report present-but-invalid
      `_timeout_seconds` / `_background` values; pipeline rejects the call
      pre-dispatch with an error naming key, value, expected type.
- [x] 4.2 Change `ComputeEffectiveTimeout` to report when the effective value
      differs from the requested value (ceiling clamp, below-floor); plumb as
      a notice, not a silent return.
- [x] 4.3 Add `Notices` accumulation to `ToolExecutionContext` and append
      notices to `resultText` at the existing `AppendModelInputHandoffWarning`
      seam (post-bounding, so notices cannot be spilled away). Clamp notice
      text steers to `_background: true` for longer work.
- [x] 4.4 Unit tests: 1200s request with 600s ceiling → executes at 600s AND
      result contains the clamp notice with `_background` steer; 10s request
      with 60s floor → executes at 60s with notice; honored 300s request → no
      notice; `_timeout_seconds: "1200ms"` → rejected pre-dispatch;
      `_background: "yes"` → rejected; `_timeout_seconds: 12.5` → rejected
      without uncaught throw; notice survives an over-budget result that
      spills.

## 5. Provider boundary: malformed arguments JSON

- [x] 5.1 In `OpenAiCompatibleChatClient`, on `TryDeserializeArguments`
      failure attach the `__netclaw_args_parse_error` sentinel (exception
      message + first 200 chars of raw payload) instead of returning null
      arguments.
- [x] 5.2 In `SessionToolExecutionPipeline`, detect the sentinel before meta
      extraction and emit a tool-result error for that call id without
      dispatching ("arguments were not valid JSON… The tool was NOT
      executed.").
- [x] 5.3 Tests: truncated args JSON → error result for the call id, no tool
      invocation; sentinel round-trips persistence and re-drives to the same
      rejection deterministically.

## 6. In-tool fixes

- [x] 6.1 `WebFetchTool`: validate `Format ∈ {absent, "raw", "text"}`; reject
      anything else (no silent raw fallback). Detect response-byte-cap hit in
      `ReadBytesWithLimitAsync` and add the truncation notice to the summary.
- [x] 6.2 `ListWebhooksTool`: honor `Filter` — `"active"` (default) filters on
      `definition.Enabled`, `"all"` returns everything, other values reject;
      echo the applied filter in the result.
- [x] 6.3 Tests: unsupported format rejects with no HTTP request; >5 MB body
      carries truncation notice, under-cap body does not; active/all/unknown
      filter scenarios.

## 7. Skill and documentation sync

- [x] 7.1 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md`:
      long-running delegation calls must use `_background: true` (phrasing
      identical to the clamp-notice steer); bump `metadata.version`.
- [x] 7.2 Verify no `Netclaw.Configuration` `*Config` property changed (no
      schema sync needed) — confirm in PR description.

## 8. Quality gates and verification

- [x] 8.1 `dotnet slopwatch analyze` — no new violations.
- [x] 8.2 `./scripts/Add-FileHeaders.ps1 -Verify` — headers on any new files.
- [x] 8.3 Run the eval suite (`./evals/run-evals.sh`) — tool definitions
      changed; add/adjust an eval case asserting the model recovers from an
      unknown-key rejection in one round-trip.
- [x] 8.4 Replay regression: drive the recorded arg shapes from session
      `D0AC6CKBK5K_1781115410_840529` (`"TimeoutSeconds":"1200"` on
      shell_execute) against the validator and assert the rejection +
      suggestion; confirm representative text-parser (lowercase-key) calls
      from session logs still bind.
- [x] 8.5 Update `SILENT_FALLBACK_AUDIT.md` rows fixed by this change with
      their resolution status.
