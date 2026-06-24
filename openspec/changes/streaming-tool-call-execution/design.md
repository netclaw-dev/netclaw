## Context

A `spawn_agent` tool call is bounded today by four uncoordinated timers: the
parent batch `ProcessingWatchdog` (`SessionConfig.ToolExecutionTimeout`, 90s),
the per-tool attempt `CancelAfter` in `ExecuteToolAttemptAsync` (90s), the
sub-agent's own flat timer (`SubAgentConfig.DefaultTimeoutSeconds`, 60s), and the
spawn `Ask` (~65s). Whichever fires first wins, so an identical task passes or
fails by timing, and a sub-agent actively streaming an LLM response is killed
anyway.

`ProcessingWatchdog` is structurally single-operation — one operation id, one
timer key. It cannot represent N concurrent tool calls. PR #1035 tried to keep
sub-agents alive by having them heartbeat the parent so it could refresh that
single watchdog; with two parallel `spawn_agent` calls a healthy sibling's
heartbeats mask a wedged one. That PR is abandoned.

The deeper problem: `INetclawTool.ExecuteAsync` returns `Task<string>`. A tool is
either pending or done — there is no liveness channel. Every long-running tool
needs a bespoke timer. This change gives every tool call a uniform liveness
channel: a stream.

## Goals / Non-Goals

**Goals:**

- Make tool-call execution streaming so liveness is uniform across all tools.
- A per-call inactivity watchdog so parallel tool calls are monitored
  independently — a healthy call cannot mask a stalled sibling.
- Move tool-call liveness out of `LlmSessionActor` and `SubAgentActor` into the
  tool-execution layer.
- Keep the existing `Task.WhenAll` parallel tool-execution model unchanged.
- Keep the ~28 existing tools and the MCP/AI adapters working with no change.
- Preserve per-call failure isolation: one tool failing never discards a
  sibling's result or fails the turn.
- Provide the streaming foundation issue #1038 (background mode) builds on.

**Non-Goals:**

- Implementing background/detached `spawn_agent` runs (#1038).
- Mapping MCP `notifications/progress` to activity items (follow-on).
- Changing the LLM-call or compaction watchdog behavior.
- Streaming intermediate tool output into the LLM context — activity items are
  ephemeral by design.
- Persisting or wire-serializing `ToolCallUpdate` items.

## Decisions

### D1. Tool execution yields `IAsyncEnumerable<ToolCallUpdate>`

**Decision:** A tool call produces a stream of `ToolCallUpdate` items: zero or
more non-terminal `ToolActivity` items (a phase label plus an optional output
chunk), then exactly one terminal `ToolCompleted` item carrying the result
string, file attachments, and any sub-agent runs/findings the current
`ToolCallResult` carries. A tool failure or watchdog timeout is surfaced by the
tool-execution layer as a terminal error result, not as an escaping exception.

**Rationale:** A stream is an ordered, cancellable, terminable liveness channel
that works for every tool, drives the per-call watchdog, and matches the shape
issue #1038 needs. It mirrors the existing LLM streaming path, which already
folds a stream of deltas into one `ChatResponse`.

**Alternatives considered:**

- Keep `Task<string>` and add a side-channel progress callback (today's
  `OnSubAgentActivity`). Rejected: ad-hoc, sub-agent-specific, and not ordered
  or terminable.
- A sub-agent-specific run registry on the session actor. Rejected: solves only
  sub-agents and re-introduces shared parent-side state.

### D2. The streaming method is a default interface method on `INetclawTool`

**Decision:** `INetclawTool` gains
`IAsyncEnumerable<ToolCallUpdate> ExecuteStreamAsync(...)` as a default interface
method. Its default body yields one `ToolCompleted` wrapping
`await ExecuteAsync(...)`. `IToolExecutor` / `DispatchingToolExecutor` gain the
matching `ExecuteStreamAsync`. Tools that benefit override the method:
`SpawnAgentTool`, `ShellTool`, `WebFetchTool`.

**Rationale:** Default interface methods may be async iterators, so all ~28
`NetclawTool<TParams>` implementations and the `McpToolAdapter` / `AIToolAdapter`
(which implement `INetclawTool` directly, not via the base) inherit streaming
with no code change. The blast radius is a contract addition plus ~3 opt-in
overrides, not a 28-tool rewrite.

**Alternatives considered:**

- A method on the `NetclawTool<TParams>` base class. Rejected: the MCP and AI
  adapters do not derive from the base and would be left out.
- A separate `IStreamingNetclawTool` opt-in interface. Rejected: the executor
  would need a type check per call; a default method is uniform.

### D3. A per-call two-phase inactivity watchdog in the tool-execution layer

**Decision:** `SessionToolExecutionPipeline.ExecuteSingleToolAsync` consumes each
tool's stream under a per-call, two-phase inactivity watchdog: a generous
*first-item* budget (time to the first `ToolCallUpdate`) and a tighter
*inter-item* budget that resets on every item. On expiry the watchdog cancels
that call's `CancellationToken` and the call yields a terminal timeout error.
The watchdog is a small, pure, `TimeProvider`-driven helper. It replaces the
flat `CancelAfter` in `ExecuteToolAttemptAsync`.

**Rationale:** Per-call monitoring means parallel tool calls are independent — a
healthy call cannot keep a stalled sibling's timer alive. The two-phase shape
mirrors the LLM watchdog (`PrefillTimeout` then `FirstTokenTimeout`). Enforcement
is centralized in one helper; a streaming tool's only obligation is to emit
activity items.

**Alternatives considered:**

- A single flat per-call timeout. Rejected: cannot distinguish a slow first
  response from a stalled stream, the same conflation the LLM watchdog already
  resolved (`two-phase-streaming-timeout`).

**Update — liveness refined to two classes (#1472 / PR #1481, "addition through
subtraction"):** the two-phase watchdog above proved to be the wrong shape for
self-monitoring tools. A self-monitoring tool (e.g. `spawn_agent`) already owns a
complete internal liveness model — its own prefill / no-progress watchdog plus a
guaranteed terminal result — so a parent watchdog is a second, redundant timer that
can only mis-fire (it was killing healthy sub-agents mid-approval). The final model:

- **Opaque** tools keep one wall-clock budget; streamed output does not extend it.
  (The inter-item reset was dropped — it let a chatty tool live forever.)
- **Self-monitoring** tools get NO parent watchdog at all. The pipeline drains the
  stream to its terminal item (`DrainToCompletionAsync`); the call is bounded only
  by the tool's own watchdog or caller (turn/user) cancellation. The `FirstItemOnly`
  startup guard is removed, and an unanswered human approval blocks the run until it
  is answered or the turn is cancelled (a foreground sub-agent waits — by design).

A startup assertion (`ToolLivenessValidator`) enforces that a tool's declared
liveness class matches its resolved mode in **both** directions, since an
unsupervised drain is only safe for a tool that genuinely owns its liveness.

### D4. `ProcessingWatchdog` reverts to LLM-only; the batch tool watchdog is removed

**Decision:** `LlmSessionActor.HandleToolCallResponse` no longer arms a
`ToolExecution` operation on `ProcessingWatchdog`. The `ToolExecution` watchdog
handling, `RefreshIfCurrent`, and `PauseToolExecutionWatchdogForApprovalWait` /
`ResumeToolExecutionWatchdogAfterApprovalWait` are removed. `ProcessingWatchdog`
governs only LLM calls and compaction.

**Rationale:** Tool-call liveness is now the tool-execution layer's job. Keeping
a separate single-operation batch watchdog re-creates the racing-timers bug and
can fail a whole turn while a tool is legitimately running.

### D5. Activity items are ephemeral — only the terminal result reaches the LLM

**Decision:** Only the terminal `ToolCompleted` result becomes the `role=Tool`
message appended to the conversation, still clamped to `maxInlineToolResultChars`.
`ToolActivity` items are consumed only by the per-call watchdog and an optional
live UI / session-output relay; they are never accumulated into the LLM context.

**Rationale:** This mirrors LLM streaming — the user may watch deltas, but the
stored message is the final assembled response. It guarantees a chatty streaming
tool (e.g. `shell_execute` stdout) cannot blow out the context window.

### D6. `spawn_agent` is a streaming tool; `SubAgentActor` keeps its inactivity watchdog

**Decision:** `SpawnAgentTool` overrides the streaming method. Instead of
`SubAgentSpawner.SpawnAsync` doing `Ask<SubAgentResult>` and blocking, the
sub-agent's progress is surfaced as `ToolActivity` items and the terminal
`SubAgentResult` as the `ToolCompleted` item. `SubAgentActor` keeps its own
internal inactivity watchdog (self-governance). The absolute wall-clock backstop
is dropped: a run is bounded by the per-call inactivity watchdog plus
`SubAgentActor.MaxToolIterations`.

**Rationale:** A sub-agent becomes an ordinary streaming tool — same liveness
model as a long shell command. Industry practice (OpenCode `steps`, Claude Code
`max_turns`) bounds agent runs by an iteration cap plus an inactivity timeout,
not a wall-clock cap; netclaw already has `MaxToolIterations`.

**Alternatives considered:**

- Keep the absolute backstop as defense-in-depth. Rejected: redundant with the
  inactivity watchdog and iteration cap, and its "must exceed inherited budgets"
  invariant is unenforceable config foot-gun.

### D7. Sub-agent recursion is denied by one resolution-time filter

**Decision:** `spawn_agent` is denied to sub-agents by the single
`SubAgentToolPolicy` denylist applied in `SubAgentSpawner.ResolveTools`. The
redundant `spawn_agent` string-compare in the `SubAgentActor` constructor is
removed.

**Rationale:** One authoritative filter at tool resolution is easier to reason
about than three overlapping string-matches. The actor trusts that
`definition.Tools` is already resolved.

### D8. Approval policy is the single authoritative tool-access boundary

**Decision:** The non-interactive safe-list auto-grant is removed from
`ToolAccessPolicy`. An unapproved tool invoked in a non-interactive session
(e.g. a reminder- or webhook-triggered sub-agent) fails closed with a legible
error that names the tool and the reason.

**Rationale:** The approval policy already defines what each audience may do;
the implicit hardcoded safe-list was a second, weaker boundary. Removing it means
everything a tool can do is inside the envelope the operator already granted.
Fail-closed with a legible error matches the repo's default-deny posture.

### D9. Per-call failures never fault the batch

**Decision:** `ExecuteSingleToolAsync` continues to catch every per-tool
exception and timeout and return it as a `role=Tool` error result keyed to that
`ToolCallId`; it never throws. `ExecuteToolsAsync` keeps `Task.WhenAll`, so all
N results — successes and errors — always reach the LLM as tool-result messages.

**Rationale:** A wedged sub-agent among N must not discard healthy siblings'
results or fail the turn. Every `tool_use` must also receive a `tool_result` for
conversation validity. Removing the batch watchdog (D4) eliminates the only path
that today bypasses this isolation.

## Risks / Trade-offs

- **[Risk]** `INetclawTool` contract change touches a core abstraction. ->
  **Mitigation:** the streaming method is a default interface method; existing
  tools and adapters are untouched and verified by a regression test.
- **[Risk]** A tool that ignores its `CancellationToken` can still hang the
  `await foreach`. -> **Mitigation:** retain a hard `Task`-level cancellation
  around stream enumeration; the per-call watchdog cancels the token and the
  enumeration is abandoned.
- **[Risk]** Removing the non-interactive auto-grant changes behavior for
  reminder/webhook-triggered sub-agents. -> **Mitigation:** intentional; the
  failure is legible and operators widen access through the persistent approval
  store.
- **[Trade-off]** A slow MCP tool that emits no progress is governed only by the
  first-item budget. -> **Mitigation:** that budget is generous and operator-
  configurable; mapping MCP progress notifications is a planned follow-on.

## Migration Plan

1. Add the `ToolCallUpdate` type and the `ExecuteStreamAsync` default interface
   method on `INetclawTool`; add it to `IToolExecutor` / `DispatchingToolExecutor`.
2. Add the per-call two-phase streaming watchdog helper and switch
   `SessionToolExecutionPipeline.ExecuteSingleToolAsync` to consume streams.
3. Remove the `ToolExecution` operation from `ProcessingWatchdog` usage in
   `LlmSessionActor`.
4. Override the streaming method for `SpawnAgentTool`, `ShellTool`, `WebFetchTool`;
   wire `SubAgentActor` progress into the spawn tool's stream; drop the absolute
   backstop.
5. Collapse sub-agent recursion to the single `SubAgentToolPolicy` filter; remove
   the non-interactive auto-grant from `ToolAccessPolicy`.
6. Add the per-call inactivity budgets to `SessionConfig` and
   `netclaw-config.v1.schema.json`.
7. Update tests, runbooks, and the eval suite.

Rollback: the default interface method makes the contract additive, so reverting
is removing the per-call watchdog and restoring the flat `CancelAfter` and the
`ToolExecution` watchdog operation.

## Open Questions

- Exact default values for the first-item and inter-item budgets, and whether
  they live on `SessionConfig` or a tool-scoped config section — resolved during
  implementation so long as the observable two-phase contract holds.
- Whether `MaxToolIterations` becomes per-`SubAgentProfile` configurable in this
  change or a follow-on — implementation may defer it.
