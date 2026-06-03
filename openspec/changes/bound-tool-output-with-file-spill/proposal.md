## Why

Large tool output is bounded inconsistently across the codebase, and the two
truncation stages that do exist disagree. `shell_execute` now bounds its pipe
reads to a head+tail window (`BoundedDrainAsync`, #1293), but the session
pipeline then re-clamps every tool result to `MaxInlineToolResultChars`
**head-only** — so the tail that `BoundedDrainAsync` worked to preserve (errors,
exit status) is discarded before the model sees it, and `stderr` can be dropped
entirely. Meanwhile two sibling paths still materialize unbounded output in
memory and can OOM a memory-limited daemon: `background_job` output capture
(#1300) and the default `file_read` path (#1301); `file_read` also returns file
contents with no secret redaction at all.

The fix is to make output bounding a single, shared, deliberate mechanism:
one budget, one policy (head + tail), and — when output exceeds the budget —
spill the full output to a session-scoped file and hand the model the path with
a hint to read ranges or `grep` instead of re-running. This is the pattern both
Claude Code and opencode ship, and it is the pattern `background-job-execution`
already half-specifies (tail + output-file path).

No dedicated PRD exists; this originates from the #1293 production OOM incident
and its review (#1300, #1301). It should be linked from a PRD if one is opened.

## What Changes

- Bound tool output and spill the overflow in **one place** —
  `DispatchingToolExecutor`, the chokepoint every tool result already passes
  through (main session and sub-agents) and where the central
  `SecretOutputRedactor.Redact` already runs. Right after redaction it windows the
  result to the tool's inline budget and, on overflow, spills the full redacted
  result to `sessionDir/tool-calls/{toolCallId}.log` with a steer to `file_read`
  (offset/limit) or `grep`. The spilled file is redacted for free.
- **Per-tool inline budgets.** Add `INetclawTool.InlineOutputBudgetChars` (default
  `0` = use the session content budget). `shell_execute` overrides to **2000**
  (verbose output the model skims); content tools (`file_read`, `web_fetch`,
  `web_search`, memory, MCP) use the **12000** content budget because the model
  fetched them to read in full.
- **Retire `ClampToolResult`.** The dispatcher is the single truncation stage;
  the head-only pipeline clamp is removed (no double-clamp).
- `SessionTuning.MaxInlineToolResultChars` stays **12000** (the content default);
  only verbose tools opt down. `ToolConfig.MaxOutputChars` becomes the **capture
  ceiling** (32000 → **256000**) — the memory/disk bound on what a tool captures.
- Tools shrink to "bound capture (OOM) + return raw": `shell_execute` drains both
  pipes to the ceiling and returns the combined output (no per-tool window /
  redact / spill); `file_read` reads a bounded head (no `ReadAllTextAsync`) and
  returns it (closes #1301 OOM). The central dispatcher redaction already covered
  `file_read`, so its redundant redact-on-read is removed (#1301's redaction half
  was a false alarm).
- `background_job`: bounded pipe drain via the shared reader + a capture-ceiling
  marker (closes #1300 OOM). It keeps its own on-disk log, not the dispatcher
  spill.

### Out of scope (this change)

- Spill-file lifecycle/cleanup (tracked separately; the session-log cleanup
  issue owns retention/sweep).
- A byte-complete (unbounded) spill: capture is bounded by `MaxOutputChars`, so a
  multi-hundred-MB flood is captured head+tail, not in full. See design D7/D8.
- Inferring from a shell command's AST that its output equals an existing file
  (rejected — output ≠ a referenced file once piped/filtered).
- **Media egress (#1296)** — image/AV bytes sent *to* the model. It shares the
  "don't `ReadAllBytes` a huge thing" lesson but needs a different fix
  (downscale/streamed-encode/provider file APIs), so it stays a separate change;
  this one closes only the **text** tool-output paths (#1300, #1301).

## Capabilities

### New Capabilities

- `bounded-tool-output`: the cross-cutting contract — `DispatchingToolExecutor`
  bounds every (already-redacted) tool result to the tool's inline budget and
  spills the overflow to a session file with a steer; per-tool budgets
  (`InlineOutputBudgetChars`, content default vs verbose override); a shared
  bounded-output reader; the capture ceiling. Bounded memory on every path.

### Modified Capabilities

- `netclaw-tools`: `shell_execute` and `file_read` shrink to bounded-capture-and-
  return; `shell_execute` declares the small verbose budget and bounds combined
  stdout+stderr; `file_read` reads a bounded head (no full materialization). The
  truncation requirement moves from a per-tool indicator to the central
  dispatcher's window + spill + steer.
- `netclaw-session`: tool-result inlining no longer clamps in the pipeline
  (`ClampToolResult` removed); the dispatcher is the single bounding+spill stage.
- `background-job-execution`: output capture SHALL bound memory (shared reader +
  ceiling marker) rather than buffering the full output before trimming.

## Impact

- **Code:** `DispatchingToolExecutor` (bound+spill), `ToolOutputSpill` (the
  bound+spill helper), `INetclawTool`/`NetclawTool` (`InlineOutputBudgetChars`
  rail), `ShellTool` (declares 2000; bound combined; drop redact/spill),
  `FileReadTool` (bounded head; drop redundant redaction), `BoundedOutputReader`
  (shared reader), `BackgroundJobExecutionActor` (bounded drain + marker),
  `SessionToolExecutionPipeline` (`ClampToolResult` removed), `ToolExecutionContext`
  (`MaxInlineToolResultChars` content budget; `ToolCallId` removed — the
  dispatcher uses `toolCall.CallId`).
- **Config:** `SessionTuning.MaxInlineToolResultChars` stays 12000 (content);
  `ToolConfig.MaxOutputChars` 32000 → 256000 (capture ceiling; schema default
  updated).
- **Security:** redaction unchanged in placement (central dispatcher) — the spill
  file is redacted because redaction runs before it; spill files live under the
  session directory and inherit its access scope.
- **Operational:** large tool outputs produce a session-dir `.log` the agent
  reads on demand; the model is steered toward ranged reads/`grep`. Closes #1300
  and #1301; resolves the #1293 review's two-stage-truncation tension.
- **Evals/skills:** tool-output behavior changes — update `netclaw-operations`
  (done) and any eval cases asserting on tool-result truncation/format.
