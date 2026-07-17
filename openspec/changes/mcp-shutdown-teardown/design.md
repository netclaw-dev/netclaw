## Context

`McpClientManager` is registered as an `IHostedService` (and singleton
`IMcpReconnectable`) before `services.AddAkka(...)` in
`ConfigureDaemonServices` (`src/Netclaw.Daemon/Program.cs`). .NET's generic
host stops hosted services in reverse registration order (LIFO). Akka's own
hosted service (`Akka.Hosting.AkkaHostedService`, registered internally by
`AddAkka`) is therefore stopped *before* `McpClientManager`. Its `StopAsync`
override does not consult the `CancellationToken` it's given — it always
awaits `CoordinatedShutdown.Run(...)` to natural completion:

```csharp
public virtual async Task StopAsync(CancellationToken cancellationToken)
{
    if (CoordinatedShutdown == null) return;
    await CoordinatedShutdown.Run(CoordinatedShutdown.ClrExitReason.Instance)
        .ConfigureAwait(false);
}
```

`CoordinatedShutdown`'s `before-service-unbind` phase is where
`drain-llm-sessions` runs (`Program.cs` ~1071), bounded to
`DaemonConfig.BoundedDrainTimeout` (190s, per #1673) but not shorter than
that by design — sessions mid-LLM-call need room to finish. Only once that
phase (and the rest of `CoordinatedShutdown`) completes does
`AkkaHostedService.StopAsync` return, unblocking the host's stop sequence to
reach `McpClientManager.StopAsync`. Today that method disposes clients with a
plain `foreach` + `await` — sequential, not parallel — each client's SDK
dispose being a graceful wait then a process-tree kill (10s + 10s per
server). `McpReconnectionService` (a `BackgroundService` registered
immediately after `McpClientManager`) polls every 30s and calls
`TryReconnectAsync` for any server in `Unreachable` state; because it too
sits before `AddAkka` in registration order, its own `StopAsync` (which
cancels its `stoppingToken`) doesn't fire until after `AkkaHostedService`
stops — so it keeps ticking, and could reconnect (launching a new child
process) for the entire ~190s drain window, independent of this change.

MCP server state was established as daemon-scoped (not session-isolated) by
#1636 (`2026-07-14-make-stdio-mcp-process-bound`); that change's design.md
explicitly deferred shutdown-sequencing changes as out of scope. This design
picks that up.

## Goals / Non-Goals

**Goals:**

- Start MCP child-process teardown as early in the shutdown sequence as the
  host lifecycle allows, so it runs concurrently with session drain instead
  of strictly after the full Akka `CoordinatedShutdown` completes.
- Make teardown idempotent so it is safe to trigger from an early hook and
  still let the existing `IHostedService.StopAsync`/`IDisposable.Dispose`
  path run unconditionally afterward.
- Close the reconnect-during-shutdown gap: once teardown starts, no code
  path (invocation retry, or `McpReconnectionService`'s poll) may create a
  new client or child process.
- Bound total MCP teardown time by the slowest single server, not the sum
  across configured servers.
- Preserve the existing clean-tool-error behavior for a tool call that loses
  its transport mid-flight — this already exists at the `McpToolAdapter`
  layer and needs no new plumbing, only confirmation it still applies.

**Non-Goals:**

- Lazy/on-demand MCP process startup or idle-process reclamation.
- Per-session MCP process isolation.
- Any change to `HostOptions.ShutdownTimeout`, `DaemonConfig.*ShutdownBudget*`,
  or the Akka `before-service-unbind` phase timeout — those stay exactly as
  #1673 left them. This change only moves *when MCP teardown starts*
  relative to those existing budgets, not the budgets themselves.
- Solving the CLI force-kill orphan gap from inside the daemon (see Risks).
- Changing the per-client SDK dispose timing (10s graceful + 10s kill) or the
  `StdioClientTransportOptions.ShutdownTimeout` value.

## Decisions

### Trigger teardown from `IHostApplicationLifetime.ApplicationStopping`

The generic host fires `ApplicationStopping` at the very start of
`Host.StopAsync`, before any hosted service's `StopAsync` is invoked
(including `AkkaHostedService`'s). Registering a callback there
(`appLifetime.ApplicationStopping.Register(...)`, or an
`ApplicationStopping.Register` wired in during DI setup) gives
`McpClientManager` a shutdown signal that fires regardless of which hosted
service stops first, and regardless of hosted-service registration order —
so this fix does not depend on reordering `McpClientManager`'s or `AddAkka`'s
registration (a more fragile change that would need re-verifying on every
future service addition).

`ApplicationStopping` also fires for every shutdown path that calls
`IHostApplicationLifetime.StopApplication()` or lets the host observe
SIGTERM/Ctrl-C — including the `daemon stop` graceful path and the config-reload
restart path (`DaemonRestartCoordinator`) — so this is a single hook, not one
more of several call sites to keep in sync.

Alternative considered: reorder hosted-service registration so
`McpClientManager` stops before `AkkaHostedService`. Rejected — LIFO ordering
is implicit and easy to silently break with a future registration reorder;
`ApplicationStopping` is an explicit, order-independent signal for exactly
this purpose.

### Idempotent teardown via a shared, memoized operation

`McpClientManager` gains one internal `TeardownAsync()` that both the new
`ApplicationStopping` callback and the existing `StopAsync`/`Dispose` route
through. The first caller performs the real work (disposes each client,
clears `_clients`/`_sharedToolFunctions`); it also sets a `Volatile`
`_stopping` flag (see next decision) synchronously, before any awaits, so a
racing second call can observe it immediately. The teardown work itself is
memoized behind a single cached `Task` so a second caller awaits the same
completion instead of redoing (or racing) disposal — this is the same
"first caller does the work, others await it" shape already used for
one-time async initialization elsewhere in the codebase, applied here to
one-time async teardown.

This means `StopAsync` (still called by the host afterward, since removing
the `IHostedService` registration is not proposed — the host's own lifecycle
guarantees are worth keeping as a backstop) becomes a no-op await on already-
completed work in the common case, and the log line for "MCP client shut
down" is only emitted once, by whichever call actually performed the
dispose.

Alternative considered: guard with `Interlocked.CompareExchange` on a bool and
skip entirely on the second call without awaiting anything. Rejected —
if `ApplicationStopping`'s teardown is still in flight when `StopAsync` runs
(a real possibility, since `StopAsync` can be reached quickly if other
hosted services stop fast), a bare skip would let `StopAsync` return before
disposal is actually complete, which could race the process exit against
still-running child-process kills. Awaiting the same memoized task avoids
that.

### Stopping flag gates `ConnectAsync`/`TryReconnectAsync`

`ConnectAsync` and `TryReconnectAsync` both check the same `_stopping` flag
set by teardown and return `false`/no-op immediately if set, before touching
the transport or spawning a process. This is the single choke point both
existing reconnect callers go through:

- `InvokeSharedAsync`'s failure-recovery path (a tool call whose function
  invocation throws attempts `TryReconnectAsync` before surfacing the
  error) — during shutdown, the exception it's recovering from may be the
  very disposal this change introduces; without the guard, that recovery
  path would spin up a brand-new child process seconds before the daemon
  exits.
- `McpReconnectionService`'s periodic poll — already able to run for the
  full drain window today (see Context); the guard closes this off as a
  side effect without requiring any change to that service itself, keeping
  this change scoped to `McpClientManager`.

Alternative considered: only guard `McpReconnectionService` (e.g., stop its
background loop from the same `ApplicationStopping` hook). Rejected — that
leaves the tool-call retry path unguarded, and duplicates the "are we
shutting down" signal into a second place instead of one shared flag in the
component that already owns client lifecycle.

### Parallel per-server teardown

`TeardownAsync()` disposes all clients via a `Task.WhenAll` projection
instead of the current sequential `foreach` + `await`. Each server's dispose
keeps its own try/catch so one server's disposal failure doesn't prevent or
delay others' (matching today's per-iteration try/catch, just no longer
serialized). With `N` configured servers this bounds total MCP teardown
latency at roughly the slowest single dispose (~20s worst case) instead of
`N × 20s` — meaningful once `ApplicationStopping` makes MCP teardown a
concurrent, budget-competing activity alongside session drain rather than an
afterthought that ran once the drain had already finished.

Alternative considered: leave teardown sequential since it now starts
earlier and usually has more elapsed time to work with. Rejected — "usually"
is exactly the kind of soft guarantee the #1664/#1665 postmortems warned
against; a deployment with several configured MCP servers and a slow drain
could still exhaust the remaining budget under sequential disposal, and
parallelizing is a small, low-risk, independently testable change.

### In-flight tool call failure path: no new plumbing required

`McpToolAdapter.ExecuteAsync` already wraps `_invoker.InvokeAsync(...)` in a
try/catch that converts any exception into
`"Error: MCP tool '{Name}' failed: {ex.Message}"` — a clean, attributed tool
result, not a hang or an unhandled fault. When teardown disposes a client
that has an in-flight call, the SDK's own graceful-then-kill dispose timeline
determines how soon that call's underlying transport read/write faults; that
fault propagates through `InvokeSharedAsync` → `McpToolAdapter.ExecuteAsync`
into the existing clean-error path unchanged. This satisfies the "no silent
fallback" bar (the session sees a loud, attributed error) without adding any
new exception handling. The bound on "how soon" is inherited from the
existing per-client SDK dispose timeout, not newly introduced by this
change — see Risks for why that upper bound (not the guaranteed lower bound)
is what we can actually promise here.

### Force-kill orphan gap: not solved from inside the daemon

The `netclaw daemon stop` force-kill path (#1665's `CliForceKillBudget`
escalation) sends `SIGKILL` to the main daemon PID from the CLI process. A
`SIGKILL` is not observable by the target process — nothing inside the
daemon, including this change's `ApplicationStopping` hook, ever runs, because
`ApplicationStopping` requires a graceful stop signal (SIGTERM or an explicit
`StopApplication()` call) to reach the host in the first place. The only
things that can reap the orphaned MCP child tree in that case are external to
the daemon process: systemd's cgroup-wide kill on unit stop (already covers
systemd-managed installs), or an external supervisor doing the same. In a
non-systemd deployment (bare `netclawd`, container entrypoint without a
process-group reaper, manual dev shell), a force-killed daemon still orphans
its MCP children after this change, exactly as it does today. This change
narrows the *window* in which force-kill is reached at all (graceful teardown
now finishes sooner, so fewer stops escalate to force-kill in practice) but
does not close the gap once force-kill happens. CLI-side child-PID tracking
(the CLI recording and killing MCP child PIDs itself) could close this
residual gap; it is out of scope here per the proposal and noted for future
work.

## Risks / Trade-offs

- **In-flight call failure timing is inherited, not new** → We bound "when
  does an in-flight call fail" by the same SDK dispose timeout that already
  exists (10s graceful + 10s kill per server), not something this change
  invents. If that upper bound proves too slow in practice (e.g., a session
  actor's own turn timeout is shorter and fires first, which is fine and
  independent), tightening it is a follow-up, not part of this change.
- **Idempotency race is real, not hypothetical** → `ApplicationStopping` and
  the host's normal hosted-service stop sequence can both reach
  `McpClientManager` in a short window on a fast shutdown. The memoized-task
  design (see Decisions) is chosen specifically so both paths converge on one
  completion instead of a bare double-dispose or a skip that returns before
  work is done. This is exactly the kind of concurrency detail that should be
  scrutinized in review and covered by a real (not simulated) double-teardown
  test using `McpSmokeHarness`'s spawned child processes.
- **Guarding reconnect could mask a legitimate late reconnect need** →
  Once teardown starts there is no legitimate reason to reconnect (the daemon
  is exiting), so returning `false`/failing fast is correct; the risk is
  narrow (making sure the guard only engages once teardown has actually
  begun, not preemptively during normal operation).
- **Residual non-systemd force-kill orphan gap remains** → Documented above
  rather than glossed over; systemd-managed installs are covered by the
  existing cgroup sweep, and this change reduces how often force-kill is
  reached, but does not eliminate the gap for non-systemd deployments.
  CLI-side child-PID tracking is the follow-up that would close it, called
  out as future work, not silently implied as solved.

## Migration Plan

No configuration or persisted-state migration. Deploying this change moves
*when* MCP teardown starts and *how* per-server disposal is scheduled;
external behavior (schema, tool names, grants) is unchanged. Rollback
restores the prior strictly-sequenced, sequential-per-server teardown with no
data implications either direction.

## Open Questions

None for this change.
