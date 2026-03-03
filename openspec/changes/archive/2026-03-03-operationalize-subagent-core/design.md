## Context

SubAgentActor and Memorizer-backed `store_memory` / `search_memories` tools
landed in the `feature/subagent-core` PR. The mechanism works end-to-end (smoke
tested against qwen3.5:9b + live Memorizer), but there is no user-facing path
to enable it, no observability when a subagent runs, and timeouts are hardcoded
constants. This design covers the five changes needed to operationalize
subagents for real use.

### Current state

- `netclaw init` has 8 wizard steps. None configure memory.
- `Memory.Provider` defaults to `"files"` — Memorizer requires manual JSON edit.
- SubAgentActor logs lifecycle events via Akka's `ILoggingAdapter` (daemon
  log only). No structured events reach session subscribers.
- Store timeout is 3 minutes, search timeout is 30 seconds — hardcoded in the
  tool classes.
- The `MemorizerConnected` context layer tells the model to use `store_memory`
  but doesn't explain the subagent delegation or latency implications.

## Goals / Non-Goals

**Goals:**

- Operator can enable Memorizer-backed memory through `netclaw init` (no JSON
  editing required).
- Health check validates Memorizer connectivity when selected.
- Session subscribers see when a subagent spawns and completes (name, tool
  count, success, duration).
- Subagent timeouts are configurable via `netclaw.json` with sensible defaults.
- Frontline model understands that memory tools delegate to curation subagents.

**Non-Goals:**

- Explicit `spawn_agent` tool (user-facing subagent spawning) — separate change.
- Disk-based subagent definitions — separate change.
- Subagent discovery context layer — separate change.
- Hot-reload of memory provider (requires daemon restart, same as today).
- Memory provider selection outside the init wizard (CLI `memory` subcommand) —
  can be added later but init wizard is the priority.

## Decisions

### D1: Wizard step placement — Memory after BrowserAutomation (step 6)

The wizard groups steps: core (Provider, ChatServices, ACL), capabilities
(Search, BrowserAutomation), deployment (Exposure, Identity, HealthCheck).
Memory is a capability step.

```
Provider=1 → ChatServices=2 → Acl=3 → Search=4 → BrowserAutomation=5
→ Memory=6 → Exposure=7 → Identity=8 → HealthCheck=9
```

`TotalSteps` becomes 9. The Memory step is never conditionally skipped — file-
backed is the always-valid default, so the step always renders (defaulting to
"Local files" selected).

**Alternative considered:** After Provider (step 2). Rejected — memory backend
choice doesn't depend on LLM provider, but grouping it with other capability
steps is more intuitive for the operator.

### D2: Memory step substeps (same pattern as Provider step)

The Provider wizard step uses substeps (0–4) for progressive disclosure:
select type → enter credentials → probe → select model. Memory follows the
same pattern:

- **Substep 0**: Select backend — "Local files (default)" or "Memorizer"
- **Substep 1** (Memorizer only): Configure connection — transport (stdio/http),
  URL or command+arguments
- **Substep 2** (Memorizer only): Validate connectivity — spinner while probing
  the MCP endpoint, show success/failure

If "Local files" is selected, substep 0 completes the step immediately (no
further input needed). This keeps the happy path fast for users who don't have
Memorizer.

### D3: Memorizer connectivity probe

The wizard needs to validate that the configured Memorizer endpoint actually
responds. Two approaches:

**Chosen: HTTP health probe.** For `Transport=http`, issue a GET to the
configured URL. For `Transport=stdio`, spawn the command and check for a valid
MCP handshake response. This mirrors how `McpClientManager` discovers tools at
daemon startup.

The probe runs with a 10-second timeout. On failure, the wizard shows the error
and lets the operator retry or fall back to local files.

### D4: Health check integration — degraded, not failed

When `Memory.Provider = "memorizer"`, the final HealthCheck step includes a
Memorizer reachability check. Result mapping:

| Memorizer reachable? | Health check status | Message |
|----------------------|---------------------|---------|
| Yes | Pass | "Memorizer connected (N tools)" |
| No | Warning (degraded) | "Memorizer unreachable — memory will use local files" |

Not a hard failure because file-backed is the implicit fallback. The operator
should know, but shouldn't be blocked from completing setup.

### D5: SubAgent observability — ToolExecutionContext callback

SubAgentActor is top-level (not a session child), so it can't emit
`SessionOutput` directly. The session needs to know about subagent activity
within the scope of a tool call.

**Chosen: Extend `ToolExecutionContext` with an optional subagent notification
callback.** The tool sets a notification when it spawns a SubAgentActor; the
session's tool execution pipeline relays it as output events.

```csharp
public sealed record ToolExecutionContext
{
    public static readonly ToolExecutionContext Empty = new();

    /// <summary>
    /// Optional callback for tools that spawn subagents.
    /// Called with (agentName, toolCount) on start,
    /// (agentName, success, duration) on completion.
    /// </summary>
    public Action<SubAgentNotification>? OnSubAgentActivity { get; init; }
}
```

The session's `ExecuteToolsAsync` pipeline wires a callback that converts
notifications to `SubAgentOutput` events and publishes them to subscribers.

**Alternative considered: EventStream pub/sub.** SubAgentActor publishes to
`ActorSystem.EventStream`, session subscribes. Rejected — requires correlation
(which session does this subagent belong to?) and introduces global coupling.
The context callback is scoped, explicit, and doesn't affect actors that don't
use subagents.

### D6: SubAgentOutput event — filtered under ToolCalls

New output events:

```csharp
public sealed record SubAgentOutput : SessionOutput
{
    public required string AgentName { get; init; }
    public required SubAgentPhase Phase { get; init; }  // Started | Completed
    public int ToolCount { get; init; }                 // on Started
    public bool Success { get; init; }                   // on Completed
    public TimeSpan Duration { get; init; }              // on Completed
}
```

Filtered under the existing `OutputFilter.ToolCalls` flag. Subagent activity
is a subcategory of tool execution — subscribers who want tool call detail
also want subagent detail. No new flag needed.

Rendering:

| Adapter | Started | Completed |
|---------|---------|-----------|
| Headless | `[subagent:start] memory-curator (3 tools)` | `[subagent:done] memory-curator (success, 12.3s)` |
| TUI | Status bar indicator | Status bar update |
| Slack | Silent (too noisy) | Silent |

### D7: Timeout configuration — SubAgents config section

```json
{
  "SubAgents": {
    "DefaultTimeoutSeconds": 60,
    "StoreMemoryTimeoutSeconds": 180,
    "SearchMemoriesTimeoutSeconds": 30
  }
}
```

Bound to `SubAgentConfig`:

```csharp
public sealed class SubAgentConfig
{
    public int DefaultTimeoutSeconds { get; set; } = 60;
    public int StoreMemoryTimeoutSeconds { get; set; } = 180;
    public int SearchMemoriesTimeoutSeconds { get; set; } = 30;
}
```

Injected into Memorizer tools via DI. When the section is absent from
`netclaw.json`, the defaults match current hardcoded values — zero behavior
change for existing installations. `netclaw doctor` validates that timeout
values are positive and within reasonable bounds (5–600 seconds).

### D8: Context layer text — mention subagent delegation

The `MemorizerConnected` context layer adds one sentence after the SAVE
paragraph:

> Note: store_memory and search_memories delegate to curation subagents that
> handle Memorizer complexity (dedup, workspace routing, relationship linking).
> These calls may take 10–30 seconds — this is normal.

This sets the frontline model's expectations so it doesn't retry or apologize
for tool call latency.

## Risks / Trade-offs

**[Risk] Wizard step count creep (8 → 9).**
→ Mitigation: Memory step defaults to "Local files" with zero further input.
Operators who don't care about Memorizer press Enter once and move on. Net
time cost: ~2 seconds.

**[Risk] ToolExecutionContext callback couples tool API to subagent concept.**
→ Mitigation: The callback is optional (`Action?`), null by default. Tools
that don't spawn subagents never see it. The `ToolExecutionContext.Empty`
singleton remains valid. This is a narrow extension, not a redesign.

**[Risk] Memorizer probe in wizard may be slow or flaky.**
→ Mitigation: 10-second timeout, clear error message, option to retry or
fall back to local files. Same UX pattern as the existing LLM provider probe.

**[Risk] SubAgentOutput events increase output volume.**
→ Mitigation: Filtered under ToolCalls (opt-in). Slack adapter suppresses
them entirely. Only headless and TUI render them, and only when the subscriber
has ToolCalls in their OutputFilter.

**[Trade-off] Per-tool timeout config vs single default.**
→ We provide both. The specific values (`StoreMemoryTimeoutSeconds`,
`SearchMemoriesTimeoutSeconds`) override `DefaultTimeoutSeconds`. Future
subagent types can add their own keys or fall back to the default. This
avoids premature abstraction (named profiles, etc.) while supporting the
two concrete cases we have today.

## Open Questions

1. **Should `netclaw doctor` validate Memorizer connectivity?** Currently
   `doctor` is offline-only (config schema validation). Adding a live
   connectivity check would make it partially online. Might be better as a
   separate `netclaw doctor --online` flag.

2. **Should the TUI chat render subagent progress?** The design has TUI
   showing a status bar indicator, but the TUI's output rendering may not
   support inline status updates during a tool call. Needs investigation of
   the Termina framework's capabilities. Can defer to "silent in TUI" if
   complex.
