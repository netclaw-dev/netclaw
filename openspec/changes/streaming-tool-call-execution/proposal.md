## Why

`spawn_agent` sub-agents are killed mid-stream by a flat ~90s timeout. A single
`spawn_agent` tool call is bounded by four uncoordinated timers — the parent
batch `ProcessingWatchdog` (`ToolExecutionTimeout`, 90s), the per-tool attempt
`CancelAfter` (90s), the sub-agent's own flat timer (`SubAgentConfig.DefaultTimeoutSeconds`,
60s), and the spawn `Ask` (~65s) — and whichever fires first wins. An identical
task passes or fails by timing, and a sub-agent actively streaming an LLM
response is killed anyway. Diagnosed from three production sessions.

The root cause is not sub-agent-specific. `INetclawTool.ExecuteAsync` returns
`Task<string>`: a tool is either pending or done, with no liveness signal in
between. Anything genuinely long-running — a delegated sub-agent, a long shell
command, a slow MCP tool — has no way to say "still working," so it must be
special-cased with bespoke timers.

A prior attempt (PR #1035) bolted a per-sub-agent heartbeat protocol onto the
parent's structurally single-operation `ProcessingWatchdog`; with two parallel
`spawn_agent` calls a healthy sibling's heartbeats keep the shared watchdog
alive and mask a wedged sibling. That PR is abandoned in favor of this change.

The fix is to make the tool-call abstraction itself streaming, so liveness is a
first-class, uniform property of every tool call and a sub-agent stops being a
special case.

## Source PRDs

- `PRD-001` (Netclaw MVP): reliable tool execution and predictable runtime
  behavior for delegated work.
- `PRD-002` (Gateway Security Envelope): default-deny, fail-closed approval as
  the single authoritative tool-access boundary.
- `PRD-006` (MCP Tool Integration): MCP server tools execute under the same
  contract as first-party tools.

## What Changes

- Tool execution becomes streaming: a tool call yields an `IAsyncEnumerable` of
  `ToolCallUpdate` items — zero or more non-terminal *activity* items, then
  exactly one terminal *completion* item carrying the result.
- The streaming method is a default interface method on `INetclawTool`; its
  default body wraps the existing `Task<string>` execution as a single terminal
  item, so every existing tool — including `McpToolAdapter` and `AIToolAdapter` —
  works unchanged. Only `SpawnAgentTool`, `ShellTool`, and `WebFetchTool` opt
  into real streaming.
- A per-call, two-phase inactivity watchdog moves into the tool-execution layer
  (`SessionToolExecutionPipeline`): a generous first-item budget, then a tighter
  inter-item budget that resets on each activity item. It replaces the flat
  per-attempt `CancelAfter`.
- `ProcessingWatchdog` reverts to governing LLM calls and compaction only. The
  parent batch tool-execution watchdog is removed; per-tool liveness is the
  tool-execution layer's responsibility, owned by neither `LlmSessionActor` nor
  `SubAgentActor`.
- `spawn_agent` becomes a streaming tool: the sub-agent's progress is surfaced as
  activity items, the terminal `SubAgentResult` as the completion item.
- **Invariant**: only the terminal completion item enters the conversation and
  the LLM context (still clamped to `maxInlineToolResultChars`). Activity items
  are ephemeral — they drive the watchdog and an optional live UI relay only.
- `SubAgentActor` keeps its own internal inactivity watchdog; the absolute
  wall-clock backstop is dropped — a run is bounded by per-call inactivity plus
  `MaxToolIterations`.
- Sub-agent recursion is denied by a single `SubAgentToolPolicy` denylist filter
  at tool resolution; the redundant `SubAgentActor` constructor string-compare is
  removed.
- The non-interactive safe-list auto-grant is removed from `ToolAccessPolicy`;
  the approval policy is the single authoritative boundary, and an unapproved
  tool in a non-interactive session fails closed with a legible error.

### In scope vs out of scope

In scope: the streaming tool-call contract, the per-call watchdog, the
`ProcessingWatchdog` revert, `spawn_agent` as a streaming tool, the recursion
and approval cleanup.

Out of scope:

- Background mode for `spawn_agent` (issue #1038) — this change provides the
  streaming foundation but does not implement detached runs.
- Mapping MCP `notifications/progress` onto activity items — MCP tools work via
  the single-item default; the progress upgrade is a follow-on.

## Capabilities

### New Capabilities

- None — this modifies existing tool-execution, sub-agent, and session behavior.

### Modified Capabilities

- `netclaw-tools`: tool execution becomes a streaming contract with a per-call
  two-phase inactivity watchdog; non-streaming tools and MCP/AI adapters inherit
  a single-terminal-item default; only the terminal result enters LLM context.
- `netclaw-subagents`: `spawn_agent` executes as a streaming tool; sub-agent runs
  are bounded by inactivity plus iteration cap with no wall-clock backstop;
  recursion is denied by one resolution-time filter.
- `netclaw-session`: `ProcessingWatchdog` no longer covers tool execution; the
  parent batch tool-execution watchdog is removed; per-tool failures never fault
  the batch.

## Impact

- **Contract**: `INetclawTool` gains a streaming default interface method;
  `IToolExecutor` / `DispatchingToolExecutor` gain `ExecuteStreamAsync`. New
  `ToolCallUpdate` type. ~28 `NetclawTool<TParams>` tools and the MCP/AI adapters
  need no change.
- **Actor**: `LlmSessionActor.HandleToolCallResponse` stops arming a
  `ToolExecution` watchdog operation; `ProcessingWatchdog.RefreshIfCurrent` and
  the approval-pause/resume of the tool watchdog are removed. `SubAgentActor`
  keeps its inactivity watchdog and loses the absolute backstop.
- **Pipeline**: `SessionToolExecutionPipeline` consumes streams with the new
  per-call watchdog; `Task.WhenAll` over independent tool calls is preserved.
- **Config**: new per-call tool-streaming inactivity budgets (first-item /
  inter-item) in `SessionConfig`; `netclaw-config.v1.schema.json` updated in the
  same change per the schema-sync rule.
- **Security**: removing the non-interactive auto-grant means a non-interactive
  sub-agent (reminder/webhook-triggered) calling an unapproved tool fails closed
  with a legible error rather than being silently auto-granted; everything a tool
  can do stays inside the approval policy the operator already granted.
- **Operations**: a wedged tool or sub-agent is caught by its own per-call
  watchdog and surfaced as a tool-result error; sibling tool calls and the turn
  survive.
- **No wire-format or persistence changes** — runtime behavior only.
- **Tests/eval**: new per-call watchdog unit tests (`FakeTimeProvider`); parallel
  spawn and mixed-batch integration tests; eval suite re-run for the tool-surface
  and `SessionConfig` change.
