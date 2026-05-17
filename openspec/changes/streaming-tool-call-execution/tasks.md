# Tasks: Streaming Tool-Call Execution

## Phase A: Streaming contract foundation

- [ ] Add a `ToolCallUpdate` type: a non-terminal activity variant (phase label +
  optional output chunk) and a terminal completion variant (result string +
  file attachments + completed sub-agent runs + accepted findings)
- [ ] Add `ExecuteStreamAsync` to `INetclawTool` as a default interface method —
  default body yields one terminal completion item wrapping the existing
  `ExecuteAsync(arguments, context, ct)`
- [ ] Add `ExecuteStreamAsync` to `IToolExecutor`; implement in
  `DispatchingToolExecutor` (authorize, resolve, surface the tool's stream,
  redact secrets per item, log)
- [ ] Verify build clean (0 warnings); the ~28 `NetclawTool<TParams>` tools and
  `McpToolAdapter` / `AIToolAdapter` compile with no change
- **Acceptance:** a tool that does not override `ExecuteStreamAsync` produces
  exactly one terminal completion item carrying its current result

## Phase B: Per-call streaming watchdog

- [ ] Add a two-phase `StreamingToolWatchdog` helper (first-item budget +
  inter-item budget that resets on each item), `TimeProvider`-driven, with no
  Akka or actor dependency
- [ ] Switch `SessionToolExecutionPipeline.ExecuteSingleToolAsync` to consume
  `ExecuteStreamAsync` under the per-call watchdog; remove the flat `CancelAfter`
  in `ExecuteToolAttemptAsync`
- [ ] On budget expiry: cancel the call's token; yield a terminal error result
  naming the tool and the timeout (keyed to the tool-call id)
- [ ] Add first-item and inter-item tool inactivity budgets to `SessionConfig`,
  `RawSessionConfig`, and `BindFromConfiguration`
- [ ] Update `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json`
  with the new properties (defaults included; schema-sync rule)
- [ ] Verify build; `Task.WhenAll` over independent tool calls still drives
  `ExecuteToolsAsync`
- **Acceptance:** a stalled stream trips the inter-item budget; a slow first
  item trips the first-item budget; a healthy parallel call is unaffected

## Phase C: Revert ProcessingWatchdog to LLM-only

- [ ] `LlmSessionActor.HandleToolCallResponse` no longer arms a `ToolExecution`
  operation on `ProcessingWatchdog`
- [ ] Remove `ToolExecution`-operation handling, `ProcessingWatchdog.RefreshIfCurrent`,
  and `PauseToolExecutionWatchdogForApprovalWait` /
  `ResumeToolExecutionWatchdogAfterApprovalWait`
- [ ] Confirm `ProcessingWatchdog` governs only `LlmCall` and `Compaction`
- [ ] Verify build; update/trim watchdog tests that asserted tool-execution
  watchdog behavior
- **Acceptance:** dispatching a tool batch arms no processing-watchdog operation;
  a long tool call does not trip a session-level timeout

## Phase D: spawn_agent as a streaming tool

- [ ] `SpawnAgentTool` overrides `ExecuteStreamAsync`; route `SubAgentActor`
  progress into a `Channel<ToolCallUpdate>` consumed as the tool's stream
- [ ] `SubAgentSpawner` surfaces the run as a stream instead of a blocking
  `Ask<SubAgentResult>`; terminal `SubAgentResult` becomes the completion item
- [ ] `SubAgentActor` keeps its own internal inactivity watchdog; remove the
  absolute wall-clock backstop and its timer
- [ ] Verify build
- **Acceptance:** a `spawn_agent` call emits activity while the sub-agent works;
  two parallel `spawn_agent` calls with one wedged — the wedged one times out
  independently, the healthy one returns, both tool-result messages reach the LLM

## Phase E: Recursion and approval cleanup

- [ ] Collapse sub-agent recursion to the single `SubAgentToolPolicy` denylist in
  `SubAgentSpawner.ResolveTools`; remove the `spawn_agent` string-compare in the
  `SubAgentActor` constructor
- [ ] Remove the non-interactive safe-list auto-grant from `ToolAccessPolicy`;
  ensure an unapproved tool in a non-interactive session fails closed with a
  legible error naming the tool and the reason
- [ ] Verify build
- **Acceptance:** `spawn_agent` is absent from any resolved sub-agent tool set; a
  non-interactive sub-agent calling an unapproved tool fails with a legible error

## Phase F: Opt-in streaming tools

- [ ] `ShellTool` overrides `ExecuteStreamAsync` to emit stdout/stderr as activity
  items; the terminal item carries the assembled, clamped result (replaces
  buffer-everything-then-truncate)
- [ ] `WebFetchTool` overrides `ExecuteStreamAsync` to emit fetch-progress
  activity items (optional within this change)
- **Acceptance:** streamed shell output appears as activity items only; the LLM
  still receives one clamped terminal result per call

## Phase G: Tests, docs, eval

- [ ] Unit tests for `StreamingToolWatchdog` with `FakeTimeProvider` —
  deterministic, no `Task.Delay` (per `CLAUDE.md` testing rules)
- [ ] Regression test: a non-streaming tool yields exactly one terminal item
- [ ] Integration test: two concurrent `spawn_agent` calls, one wedged — wedged
  one caught independently, healthy result + timeout error both reach the LLM
- [ ] Integration test: mixed batch `[spawn_agent, hung tool]` — the hung tool is
  still caught; no whole-batch failure
- [ ] Update `docs/runbooks/subagents.md` and any tool-timeout operator guidance
- [ ] Update the `netclaw-operations` system skill if tool/timeout guidance
  changed (System Skills Sync Rule)
- [ ] `dotnet slopwatch analyze` — no new violations; `./scripts/Add-FileHeaders.ps1 -Verify`
- [ ] Run `./evals/run-evals.sh` (tool surface + `SessionConfig` changed)
- **Acceptance:** full `Netclaw.Actors.Tests` / `Netclaw.Configuration.Tests`
  suites pass; eval suite passes; manual repro confirms a heavy `spawn_agent` no
  longer dies mid-stream and parallel spawns with one wedged do not hang the turn
