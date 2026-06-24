# SPEC-016: Tool Liveness And Stall Detection

Source PRDs: `PRD-001`, `PRD-006`

Status: Planning note for issue #1450. The corresponding OpenSpec artifacts must
be updated through the OpenSpec workflow before implementation is closed.

> **Superseded in part by #1472 / PR #1481 ("addition through subtraction").** The
> "parent first-item startup guard for self-monitoring tools" described below was
> removed. The parent no longer supervises self-monitoring tools at all — they own
> their liveness end to end and are drained with no parent watchdog, bounded only by
> their own internal watchdog or caller (turn/user) cancellation; an unanswered human
> approval blocks the run until it is answered or the turn is cancelled. The
> authoritative, reconciled spec is the `Per-call liveness by tool class` requirement
> in the `streaming-tool-call-execution` OpenSpec change
> (`specs/netclaw-tools/spec.md`). Treat the parent-startup-guard clauses below as
> historical.

## Purpose

Define the simplified stall-detection contract for tool calls. The goal is to
make long-running tool calls reliable without adding another competing timeout or
turning streamed output into a fake progress signal.

## Problem

`spawn_agent` is currently consumed through the same generic streaming-tool
watchdog as ordinary tools. The parent session uses
`Session.ToolExecutionTimeoutSeconds` as a reset-on-item inactivity budget. That
means a healthy sub-agent can be killed when it opens a quiet window longer than
the parent budget, even though the child actor has its own progress-aware
watchdogs.

The wrong abstraction is "did the stream emit anything?" The useful question is
"who can actually tell whether this operation has stalled?"

## Decision

Tool calls SHALL use two liveness classes.

### Opaque Tools

An opaque tool does not expose reliable internal stall detection. The parent
tool-execution pipeline SHALL apply one explicit wall-clock budget to the whole
call. Streaming output, if any, is display-only and SHALL NOT extend the budget.

Default/generated tools are opaque unless they opt into another mode.

Examples:

- MCP tools with no mapped MCP progress notification
- most generated first-party tools
- `web_fetch`
- `shell_execute`

For `shell_execute`, stdout and stderr may still be streamed to subscribers as
live output, but a command that prints forever is not making proven forward
progress. `_timeout_seconds` or `Session.ToolExecutionTimeoutSeconds` remains the
process wall-clock budget.

### Self-Monitoring Tools

A self-monitoring tool owns its own stall detection because the worker can see
more than the parent can. The parent session SHALL keep ownership of
cancellation, persistence, and final turn response, but it SHALL NOT run the
generic inter-item inactivity watchdog after the tool has produced its first
sign of life.

The parent SHALL still apply a startup guard: a self-monitoring tool must produce
its first stream item within the existing startup/first-item budget. This bounds
the irreducibly blind window where the parent does not know whether the tool
actually began executing.

`spawn_agent` is the first self-monitoring tool. Its child `SubAgentActor`
already owns:

- wait-for-first-delta prefill budget
- inter-delta liveness budget after model output starts
- keepalive-immune no-progress budget
- approval-wait suspension and external cancellation
- tool-iteration cap

If a sub-agent stalls, the child actor SHALL complete the `spawn_agent` call with
a failed `SubAgentResult`. The parent records that terminal tool result and
continues the turn according to the normal tool-batch rules.

## Non-Goals

- No new coarse parent backstop in this change.
- No new taxonomy of progress event records in this change.
- No requirement for every tool to emit progress events.
- No MCP progress mapping in this change.
- No behavior where stdout, stderr, or heartbeat output extends an opaque tool's
budget.

## Runtime Contract

`INetclawTool` SHOULD expose a liveness classification with an opaque default.

```csharp
public enum ToolLivenessMode
{
    Opaque,
    SelfMonitoring
}
```

For source-generated tools, the Roslyn generator may generate the overridden
liveness property from `NetclawToolAttribute`, but the default remains `Opaque`.
The generator can enforce that any generated first-party tool declaring
`SelfMonitoring` also overrides `ExecuteStreamAsync`.

The tool-execution pipeline SHALL choose the watchdog shape from the resolved
tool's liveness mode:

- `Opaque`: wall-clock budget for the whole call. Stream items do not reset it.
- `SelfMonitoring`: first-item startup guard only. After the first item, parent
  liveness is disabled for that call and child/tool cancellation remains linked
  to parent turn cancellation.

Approval waits remain intentional suspension owned by the layer that issued the
approval request. For sub-agents, that is the live `SubAgentActor` run.

## Shell Example

`shell_execute` remains opaque.

Expected stream shape:

1. Arguments and policy are validated.
2. The process starts.
3. Optional stdout/stderr chunks are streamed for user-visible output.
4. The process exits, is killed by the wall-clock budget, or is cancelled.
5. The terminal `ToolCompletedUpdate` carries the model-facing result.

The stdout/stderr chunks are not progress for stall detection. A command such as
`while true; do echo .; sleep 1; done` must still be killed by the wall-clock
budget.

## Sub-Agent Example

`spawn_agent` is self-monitoring.

Expected stream shape:

1. The parent resolves the agent and asks the session actor to create the child.
2. The child accepts `RunSubAgent` and emits the first stream item, such as
   `calling the model`.
3. The parent first-item guard is satisfied and parent inter-item liveness is no
   longer applied to this call.
4. The child watchdog governs prefill, model deltas, keepalive-only wedges,
   tool-loop progress, approval waits, cancellation, and iteration exhaustion.
5. The terminal `ToolCompletedUpdate` carries either a successful sub-agent result
   or a failed sub-agent result produced by the child.

## Validation Strategy

Validation must prove the failure mode directly: a self-monitoring sub-agent can
legitimately remain quiet longer than `Session.ToolExecutionTimeoutSeconds`
without the parent killing it, while opaque tools remain bounded.

### Unit And Contract Tests

- `StreamingToolWatchdog` or its replacement SHALL have deterministic tests with
  `FakeTimeProvider` proving opaque calls use wall-clock timeout, not
  reset-on-output inactivity.
- A chatty opaque stream SHALL still time out at the wall-clock budget even when
  it emits output items before the budget expires.
- A self-monitoring stream SHALL fail if no first item arrives before the startup
  guard.
- A self-monitoring stream SHALL remain alive after its first item even when no
  later item arrives before the opaque default budget.
- Parallel calls SHALL be independent: a self-monitoring call that disables its
  parent inter-item liveness must not extend or disable an opaque sibling's
  budget.
- The tool generator SHALL prove generated tools default to `Opaque` and that
  `spawn_agent` explicitly resolves as `SelfMonitoring`.

### Actor Integration Tests

- A `spawn_agent` call with `Session.ToolExecutionTimeoutSeconds` set shorter
  than the child prefill budget SHALL not be cancelled by the parent after the
  first child stream item.
- A silent child prefill SHALL be terminated by the child prefill watchdog, and
  the resulting `spawn_agent` tool result SHALL contain the child timeout reason,
  not the parent generic `produced no activity` timeout.
- A keepalive-only child stream SHALL be terminated by the child no-progress
  watchdog.
- A sub-agent approval wait longer than the parent tool timeout SHALL remain
  pending until approval, denial, approval timeout, or parent cancellation.
- Three parallel `spawn_agent` calls SHALL produce one terminal tool result per
  call when one child stalls and the others complete.

### Session And Persistence Tests

- `ActiveToolBatchTracker` SHALL not complete the tool batch until every expected
  call id has one recorded terminal tool result and the execution task has
  finished.
- A timed-out child sub-agent SHALL be recorded as a `ToolCallRecorded` event for
  the `spawn_agent` call id, not as a whole-turn `ToolExecutionFailed`.
- Recovery from `ToolBatchStarted` with missing tool results SHALL remain loud and
  deterministic; side-effecting tools must not be silently re-run.

### Diagnostics And Manual Repro

- Logs SHALL correlate parent session id, sub-agent run id, child watchdog
  reason, and terminal `spawn_agent` result.
- Manual repro should run parallel `spawn_agent` calls where one child opens a
  quiet window longer than the parent tool timeout. The expected result is no
  parent `produced no activity` failure; either the child completes or the child
  watchdog reports the stall.

## Implementation Order

1. Add the liveness classification with opaque default.
2. Mark `spawn_agent` as self-monitoring.
3. Change the tool pipeline to apply wall-clock budget for opaque tools and
   first-item-only guard for self-monitoring tools.
4. Keep shell opaque and ensure streamed stdout/stderr does not reset the budget.
5. Add the tests listed above.
6. Update OpenSpec artifacts through the OpenSpec workflow and then sync stale
   main specs that still describe tool timeouts as whole-batch failures.
