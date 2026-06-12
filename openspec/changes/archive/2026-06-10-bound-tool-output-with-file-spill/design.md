## Context

Three tool paths capture external output and feed it to the model:
`shell_execute` (process pipes), `background_job` (process pipes, long-running),
and `file_read` (a file on disk). Originally they bounded output inconsistently:

- `shell_execute` drained its pipes into a head+tail window capped at
  `ToolConfig.MaxOutputChars` (32000) via `BoundedDrainAsync` (#1293), in the tool.
- The session pipeline then re-clamped **every** tool result to
  `SessionTuning.MaxInlineToolResultChars` (12000), **head-only**, in
  `SessionToolExecutionPipeline.ClampToolResult`.
- `background_job` and the default `file_read` path did not bound memory at all —
  they materialized the full output/file as a managed string before trimming
  (#1300, #1301).

Two truncation stages that disagreed on budget (32000 vs 12000) and policy
(head+tail vs head-only), plus two unbounded OOM paths. Both Claude Code and
opencode ship the same "bound inline, spill the rest to a file, steer the model
to read ranges / grep" pattern; this change adopts it.

**The load-bearing discovery (from the implementation review):** every tool
result already funnels through **one chokepoint** — `DispatchingToolExecutor`,
which both the main session pipeline and sub-agents call to run a tool, and which
**already redacts every result centrally** (`SecretOutputRedactor.Redact`, before
`ClampToolResult` ever runs). That makes the dispatcher — not the tool, not the
pipeline clamp — the correct home for bounding + spill: it sees every result, for
every audience, already redacted. Putting it anywhere else (per-tool, or in the
main-session-only `ClampToolResult`) either duplicates the logic or misses the
sub-agent path.

## Goals / Non-Goals

**Goals:**

- One place that bounds tool output to an inline budget and, on overflow, spills
  the full (redacted) output to a session file with a steer — uniform for the
  main session and sub-agents.
- A budget that fits the tool: small for *verbose* tools (shell), generous for
  *content* tools (file_read, web_fetch, MCP, memory) the model fetched to read.
- Bounded memory on every capture path — no path materializes arbitrarily large
  output as a managed string (closes #1300, #1301 OOM).
- A single shared bounded-output reader so the ring/window logic is reviewed and
  fixed once, not copy-pasted (the #1293 review's altitude finding).

**Non-Goals:**

- Spill-file retention / cleanup (owned by the session-log-cleanup issue).
- Streaming redaction / byte-complete spill of multi-GB floods (the capture
  ceiling bounds the spill; see D7 risks).
- A new redaction abstraction — the central `SecretOutputRedactor` already covers
  every result.
- Inferring from a shell command's AST that its output equals an existing file
  (rejected: output ≠ a referenced file once piped/filtered; false positives
  mislead).

## Decisions

### D1 — Bounding + spill live in `DispatchingToolExecutor`, the one chokepoint

Immediately after the dispatcher's existing central redaction, bound the result
to the resolved budget and, when it overflows, window it head+tail and spill the
full redacted result to a session file with a steer. Because every tool result
(main + sub-agent) passes through the dispatcher already-redacted, this is a
single uniform stage and the spilled file is redacted for free.

- *Alternative — spill in the tool:* rejected. It duplicates logic across tools,
  pushes the spill into N places, and the spill body would have to be re-redacted
  per tool.
- *Alternative — spill in `ClampToolResult` (pipeline):* rejected. That path is
  main-session only; sub-agents call the dispatcher but not the pipeline clamp, so
  this would miss them (the exact gap the review found).

### D2 — `ClampToolResult` is retired (single truncation stage)

With the dispatcher owning the bound, a second pipeline clamp would re-window an
already-windowed+steered result (slicing its tail, stacking a second marker). The
`ClampToolResult` calls and method are removed; the dispatcher is the only stage.

### D3 — Per-tool inline budgets: one content default, opt-in verbose override

The budget is a property of the tool, not a policy branch in the dispatcher.
`INetclawTool.InlineOutputBudgetChars` defaults to `0` = "use the session content
budget" (`SessionTuning.MaxInlineToolResultChars`, default **12000**). Verbose
tools override it: `shell_execute` returns **2000**, because shell output is noise
the model skims. Content tools (file_read, web_fetch, web_search, memory, MCP)
take the 12000 content budget because the model fetched them to read in full —
truncating those to a tiny window would force a wasteful extra `file_read`
round-trip. The dispatcher resolves `tool.InlineOutputBudgetChars` ?? content
default. Not a `shell` special-case, not per-tool busywork.

- *Alternative — one global `N`:* rejected. A single small `N` starves content
  tools; a single large `N` bloats shell context. Verbose-vs-content is genuinely
  a per-tool property.

### D4 — Spill file `{SessionDirectory}/tool-calls/{toolCallId}.log`

The dispatcher has `toolCall.CallId` (a method parameter) and
`context.SessionDirectory`, so it names the spill per call with no extra plumbing
on `ToolExecutionContext`. The call id is sanitized against path traversal before
use as a filename. No session dir / call id → degrade to inline-only window.

### D5 — Redaction stays central; tools and the spill helper do not redact

`DispatchingToolExecutor` already redacts every result with `SecretOutputRedactor`
before bounding, so the inline result and the spilled file are both redacted from
one pass — no new abstraction, no per-tool or per-chunk redaction. `file_read`'s
own redact-on-read was therefore redundant and is removed (#1301's "no redaction"
half was a false alarm — the dispatcher covered it; only its OOM half was real).

### D6 — Tools shrink to "bound capture (OOM) + return raw"

Tools no longer window, redact, or spill — they only bound their own *capture* for
memory safety and return the raw bounded string; the dispatcher does the rest.
`shell_execute` drains both pipes to `MaxOutputChars` and returns the combined
output. `file_read` reads a bounded head (`ReadBoundedHeadAsync`, up to
`MaxOutputChars` — never `ReadAllTextAsync`) and returns it; the dispatcher then
applies the content budget + spill uniformly. The offset/limit path
(`ReadLinesAsync`) is already bounded and unchanged.

### D7 — Combined (not per-stream) bound for `shell_execute`

`shell_execute` drains stdout and stderr each to `MaxOutputChars`, assembles the
combined output, and re-windows the **combined** back to `MaxOutputChars` so the
captured/spilled body is bounded by the ceiling (not 2×). This also resolves the
per-stream doubling the review flagged.

### D8 — `MaxOutputChars` is the capture ceiling

`ToolConfig.MaxOutputChars` (raised 32000 → **256000**) is the memory/disk bound
on what a tool captures — the body that becomes the spill. It is independent of
the inline budget `N` (which the dispatcher applies). The pipe still drains past
it to avoid child deadlock; the discarded middle leaves a `...` gap in the spill.

### D9 — `background_job` keeps its own log path

`background_job` is not an inline tool result (it delivers a tail + an on-disk log
via an actor message), so it does **not** go through the dispatcher spill. It uses
the shared `BoundedOutputReader` to drain its pipes in bounded memory (closing
#1300) and marks output that exceeded its capture ceiling.

## Risks / Trade-offs

- **Capture ceiling clips the spill for a true flood** → e.g. `cat` of a 2 GB
  file yields a ~512 KB head+tail spill with a `...` gap, not a duplicate. The
  steer says "output saved to {path}" (not "full"), honest in both cases; the
  skill steers the agent to `file_read` the source for files.
- **Verbose budget (2000) is small** → intended; shell output is skimmable and
  the full output is one `file_read`/`grep` away.
- **Lowering then restoring the default** → `MaxInlineToolResultChars` stays
  12000 (content); only `shell_execute` is aggressive. Configs that set it
  explicitly are unaffected.
- **Sub-agent spills land in the parent session dir** → acceptable; the parent
  owns the directory and its access scope.

## Migration Plan

1. Land `BoundedOutputReader` (shared reader) — no behavior change.
2. Add the per-tool budget rail (`INetclawTool.InlineOutputBudgetChars`).
3. Move bound+spill into `DispatchingToolExecutor`; retire `ClampToolResult`.
4. Shrink the tools (shell/file_read) to bound-capture-and-return; remove their
   redaction.
5. `background_job` bounded drain + ceiling marker.
6. Update the `netclaw-operations` skill + eval cases.

Rollback: re-instate `ClampToolResult` and remove the dispatcher bound+spill; the
in-tool capture bounds (the OOM fixes) remain.

## Open Questions

- **`background_job` byte-complete log:** capped at `MaxCapturedOutputChars`
  (256000); a long job's log is a head+tail view. Revisit if a user needs full
  job logs (would need a larger ceiling or streaming redaction).
- **Spill for content tools with a natural backing (`file_read`):** today the
  dispatcher may spill a bounded copy of file content. The copy is redacted and
  capped, so it is harmless but slightly redundant with the original file; left
  as-is rather than special-casing `file_read` out of the uniform path.
