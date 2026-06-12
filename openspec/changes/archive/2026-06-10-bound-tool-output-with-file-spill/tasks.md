## 1. Shared bounded-output reader (foundation, no behavior change)

- [x] 1.1 Extract `BoundedDrainAsync` from `ShellTool` into a reusable
      `BoundedOutputReader` (Netclaw.Actors/Tools), keeping the #1293 ring core
      (pooled buffer, `ValueTask` reads, block-copy tail ring)
- [x] 1.2 Expose `DrainToWindowAsync(TextReader, int budget, ct) → (string Text,
      bool Truncated)` (head+tail, in-memory only)
- [x] 1.3 Expose `DrainCaptureAsync(TextReader, int captureMax, int inlineBudget,
      ct) → (string Captured, string Inline, bool CeilingExceeded)` over a shared
      core, plus a pure `Window(string, budget)` helper; drain past `captureMax`
- [x] 1.4 Move/adapt the `BoundedDrainAsync` unit tests onto `BoundedOutputReader`
      (`DrainToWindow_*`), add `Window` + `DrainCapture` coverage
- [x] 1.5 Repoint `ShellTool` and the benchmark at
      `BoundedOutputReader.DrainToWindowAsync` with no behavior change; 40 reader +
      shell tests green

## 2. Tool-call id and inline budget plumbed to the capture layer

- [x] 2.1 Add `ToolCallId` (typed value object, nullable) to `ToolExecutionContext`
- [x] 2.2 Set `ToolCallId` in `BuildToolExecutionContext` (per-call) from `tc.CallId`
- [x] 2.3 Add `MaxInlineToolResultChars` to `ToolExecutionContext` and thread `N`
      through `BuildToolExecutionContext` so tools bound to the same `N` the
      pipeline enforces
- [x] 2.4 Carry/distinctness is compiler-enforced (per-call context) + verified
      end-to-end by the task-4 spill test ({callId}.log); skip a trivial
      assignment test per the testing guidelines. (Sub-agent/direct-construction
      contexts default to null id + 0 budget — fallback handled in task 3/4.)

## 3. Spill writer + steering message

- [x] 3.1 Add `ToolOutputSpill.RenderAsync`: redact the bounded capture once,
      window the inline from the redacted text, and write
      `{sessionDirectory}/tool-calls/{toolCallId}.log` (call id sanitized against
      path traversal). Removed the redundant `DrainCaptureAsync` — the real flow
      is DrainToWindow → redact → Window → spill (inline must come from the
      *redacted* capture), so DrainToWindow + Window + the spill helper supersede it.
- [x] 3.2 Steering message (path + "read a slice with file_read offset/limit or
      grep instead of re-running") + a "capture ceiling exceeded" note
- [x] 3.3 Tests: under-budget verbatim; over-budget spill written + redacted-on-disk
      + steer; ceiling note; no-session degrade; path-traversal call id contained.
      15 reader+spill tests pass; slopwatch clean

## 4. shell_execute adopts capture + spill

- [x] 4.1 Switch `ShellTool` to combined-capture (one shared budget across
      stdout+stderr via `DrainToWindowAsync` per stream → assembled) then
      `ToolOutputSpill.RenderAsync`, which redacts once, windows to `N`, and
      spills+steers. Removed ShellTool's own per-stream redaction and markers.
- [x] 4.2 Replaced `Output_truncation_applies` with `Large_output_spills_to_file_and_steers`
      (asserts inline head+tail + spill file + steer); kept the Windows-deterministic
      `echo`. Redaction/echo/stderr tests still pass (redaction now in RenderAsync).
- [x] 4.3 Drains-past-ceiling behavior is `DrainToWindowAsync`'s (proven by the
      reader tests); the existing cancellation/kill test covers the no-deadlock path.

## 5. background_job bounded capture (closes #1300)

- [x] 5.1 `BackgroundJobExecutionActor` now drains each stream via
      `BoundedOutputReader.DrainToWindowAsync` (capture ceiling
      `MaxCapturedOutputChars = 256000`) instead of `ReadToEndAsync` — bounded
      memory; the log is head+tail for floods larger than the ceiling. Closes #1300.
- [x] 5.2 Redact-on-write unchanged (the existing `SecretOutputRedactor.Redact`
      now runs over the bounded combined output before the log write)
- [x] 5.3 Bounding is `DrainToWindowAsync`'s (unit-tested); the existing
      BackgroundJob integration tests exercise the new drain path end-to-end.

## 6. file_read bounded reads + redaction (closes #1301)

- [x] 6.1 Default `file_read` path now uses `ReadBoundedHeadAsync` (reads up to the
      limit and stops — bounds memory AND I/O) instead of `ReadAllTextAsync` +
      `TruncateFileOutput`; no spill (the file is its own backing). Closes #1301.
      (Head-only, not head+tail: a file is read top-down via Offset/Limit, and
      head+tail would require reading the whole file to reach the tail.)
- [x] 6.2 Over-budget steer: "read a specific range with Offset and Limit, or grep"
- [x] 6.3 Redact-on-read via `SecretOutputRedactor` on both the default and the
      `ReadLinesAsync` (offset/limit) return paths; offset/limit path stays bounded
- [x] 6.4 Tests: large file returns a bounded head (first N only, not all 500
      chars) + steer; secret in a read file is redacted. 2195 actor tests pass

## 7. Config + pipeline unification

- [x] 7.1 Lowered `SessionTuning.MaxInlineToolResultChars` default 12000 → 2000
- [x] 7.2 Repurposed `ToolConfig.MaxOutputChars` as the capture ceiling, default
      32000 → 256000 (docs updated to reflect the new role)
- [x] 7.3 `ClampToolResult` now head+tail (reuses `BoundedOutputReader.Window`);
      safety net for non-shared-reader results (MCP, in-process)
- [x] 7.4 Updated `netclaw-config.v1.schema.json` MaxOutputChars default → 256000
      (MaxInlineToolResultChars schema has only `minimum:100`, which 2000 satisfies).
      363 config tests + 2194 actor tests pass; slopwatch clean

## 8. Quality gates, docs, eval, and OpenSpec close-out

- [x] 8.1 `dotnet slopwatch analyze` clean (run per group); headers on all new .cs
- [x] 8.2 Updated `netclaw-operations` skill (new "Large tool output" section:
      spill to session file, steer to ranged reads/grep, bounded job log) and
      bumped `metadata.version` 2.8.9 → 2.9.0
- [x] 8.3 Eval cases flagged + the two coverage gaps ADDED + suite run against
      spark2 (Qwen3.6-35B, openai-compatible):
      - **At-risk-but-robust (verified still green, 5/5 each):**
        `complex_diagnose_self`, `complex_gh_issues`, `complex_write_and_run`,
        `multi_turn_tool_repeat`, `multi_turn_tool_carryover`. Their asserts check
        `[tool:call] shell_execute` + a keyword, not exact tool-result content —
        so truncating output to N=2000 + spill doesn't break them.
      - **New coverage cases added** (Category 7, `evals/run-evals.sh`), both 5/5.
        They assert on OUTCOME, not mechanism: the prompts state only the goal and
        give ZERO handling hints (no mention of spill/redirect/re-run/file_read/
        Offset/grep) — coping with oversized output must come from AGENTS.md, the
        netclaw-operations skill, and the tool-result steer text, or the eval is
        just testing instruction-following. Data is a deterministic-but-opaque
        Lehmer PRNG (`x=(x*48271)%2147483647`, identical across awk impls), so a
        deep-line value is reproducible AND un-fabricatable; since one read is
        bounded to ~N inline, retrieving a deep line is only possible by paging —
        so a correct value *implies* correct handling (no mechanism assertion).
        1. `complex_large_shell_output_spill` — "run `awk '…'` and tell me the
           number on line 200" (a bare safe-verb generator, no path tokens →
           auto-approves headless; ~210 KB stdout → spills). Asserts the answer
           contains line 200's value (872671849). Whether the agent reads the
           spill or self-redirects+reads, the outcome assertion is satisfied.
        2. `complex_large_file_read_ranged` — a ~314 KB file pre-seeded by
           `start_eval_daemon` under the workspaces read-root; "list lines
           4997–5003 of <file>". Asserts the answer contains line 5000's value
           (1629331733). Line 5000 is ~52 KB in (past the inline window), so a
           correct answer proves the agent paged with `file_read` Offset/Limit.
      - **Finding (separate from this change):** the model reliably pages with
        `file_read` Offset/Limit but treats `Offset` as **0-based** despite its
        "(1-based)" param description (e.g. `Offset:4999` for "line 5000"). The
        window prompt tolerates this so the case measures bounded-output paging,
        not index arithmetic; the off-by-one itself likely warrants a clearer
        `file_read.Offset` description / operations-skill note as a follow-up.
      - Run: NETCLAW_EVAL_PROVIDER_TYPE=openai-compatible
        NETCLAW_EVAL_PROVIDER_ENDPOINT=https://spark2.testlab.petabridge.net/
        NETCLAW_EVAL_MODEL_ID=Qwen/Qwen3.6-35B-A3B-FP8
        NETCLAW_EVAL_CATEGORY="Complex Task Execution" ./evals/run-evals.sh
        → Complex Task Execution 5/5 (100%), both new cases 5/5 uncoached.
- [x] 8.4 Added `CaptureBenchmarks` confirming O(ceiling): allocation flat at
      ~1255 KB for 256K and 50M chars (vs unbounded before)
- [ ] 8.5 `openspec validate` passes; sync/archive on PR merge (not yet merged)

## 9. Architecture revision (post-implementation-review)

A max-effort review of the per-tool spill design surfaced a HIGH-severity gap
(sub-agent tool output unbounded) and that the spill was at the wrong altitude.
The change pivoted; the artifacts above describe the original per-tool approach,
the proposal/design now describe the as-built. Net deltas:

- [x] 9.1 Move bound+spill into `DispatchingToolExecutor` (the one chokepoint both
      main session and sub-agents use, already the central redaction point) —
      retire `ClampToolResult`; tools shrink to bound-capture-and-return.
- [x] 9.2 Per-tool inline budgets (`INetclawTool.InlineOutputBudgetChars`): content
      default 12000 (restore `MaxInlineToolResultChars`), `shell_execute` opts to
      2000. Content tools (file_read/web_fetch/MCP/memory) no longer truncated to
      a tiny window.
- [x] 9.3 Remove `file_read` redundant redaction (central dispatcher covers it);
      drop `ToolExecutionContext.ToolCallId` (dispatcher uses `toolCall.CallId`).
- [x] 9.4 Steer wording: "output saved to {path}" (not "full"), honest for a
      capture-ceiling-clipped flood. Rejected shell-AST file inference (output ≠ a
      referenced file once piped/filtered).
- [x] 9.5 Tests retargeted to the dispatcher (end-to-end verbose-spill, redact-
      before-spill, content no-spill); `FakeToolExecutor` mirrors the dispatcher
      post-processing. 2196 actor tests pass; slopwatch clean.
