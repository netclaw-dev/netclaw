---
name: termina-tui-patterns
description: How to do async work correctly in the Termina TUI (R3 + single-threaded render loop). Activate when editing anything under src/Netclaw.Cli/Tui/ that touches a network/disk probe, a background refresh, streaming output, spinners, or when you are tempted to write `.GetAwaiter().GetResult()` in a view-model.
---

# Termina TUI Patterns (async, R3, the render loop)

## The myth that wastes hours

> "Termina has no `SynchronizationContext`, so I can't `await` — I have to
> `.GetAwaiter().GetResult()` to stay on the loop thread."

**This is wrong, and it is the single most common mistake agents make in this
codebase.** Blocking the loop thread on a network probe freezes input *and*
rendering for the entire round-trip (the spinner stops spinning, keys queue up).
"No SyncContext" does **not** mean "no async". It means async continuations
resume on arbitrary thread-pool threads, so the continuation must publish its
result through a thread-safe boundary before the Termina loop renders or handles
input from that state.

The whole TUI already runs async the right way: `netclaw chat` streams live LLM
tokens to the screen, provider/search probes spin without blocking, and this
config editor resolves channel labels *after the page loads*. Copy those. Do not
reach for `GetResult()`.

## How Termina actually works (the mental model)

Termina (package `Termina` 0.12.1, which pulls `R3` 1.3.1) runs **one** loop:
`TerminaApplication.RunAsync` does `await foreach` over an **unbounded
`Channel<object>`**, and after every dequeued event calls `RenderCurrentPage()`.
That loop is the single-threaded *serializer* — exactly one event is processed
and one render happens at a time. It runs on a thread-pool thread with **no
installed `SynchronizationContext`** (`TerminaHostedService` launches it via
`Task.Run`).

Three consequences that define every correct pattern:

1. **`RequestRedraw()` is a redraw signal, not a general UI-thread marshal.** It
   is literally `_eventChannel.Writer.TryWrite(RedrawRequested.Instance)` and is
   safe to call from any thread. The loop later dequeues it and renders. That
   does not make unrelated mutable fields, dictionaries, lists, `ReactiveProperty`
   fan-out, focus changes, navigation, or `DynamicLayoutNode.Invalidate()` safe to
   perform from a background continuation.
2. **Input handlers run synchronously on the loop thread.** Input is delivered
   inside the loop via R3 `Subject.OnNext` (a synchronous in-line fan-out, no
   scheduler). So `Input.OfType<KeyPressed>().Subscribe(HandleKeyPress)` runs on
   the loop thread — the *synchronous prefix* of your handler is on-loop.
3. **R3 `ReactiveProperty.Value = ...` is synchronous fan-out.** If a page
   subscription invalidates a `DynamicLayoutNode`, changes focus, navigates, or
   mutates Termina nodes, that work runs on the thread that set `.Value`. Setting
   a reactive property from a background continuation is therefore an off-loop UI
   mutation unless every subscriber is known to be thread-safe.
4. **Every background-to-UI handoff needs an explicit publication strategy.** Use
   one of these, and document which one applies: locked snapshots, immutable
   replacement values, `Volatile`/`Interlocked` for scalar flags and counters, or
   a genuine loop-owned action processed by a Termina input/redraw path. Canceling
   and awaiting a background task prevents stale writers, but it is not a memory
   barrier for fields concurrently read by render/input.

On ARM64 this distinction matters. x64's stronger memory ordering can hide plain
field races; Apple Silicon will not. A field written by a background continuation
and read by render/input must be synchronized even if every local x64 test passes.

## The async shape to copy (with synchronized publish)

Use this control flow for probes and refreshes: synchronous loop-owned setup,
tracked background task, cancellation check after the await, synchronized publish,
then `RequestRedraw()`. Do not copy older examples that publish plain fields or
reactive properties off-loop without auditing their subscribers.

```csharp
private CancellationTokenSource? _probeCts;   // owned CTS
private Task? _probeTask;                      // TRACKED task (never .GetResult() it)

// Called from a synchronous (loop-thread) key/selection handler.
private void StartBackgroundProbe(/* inputs */)
{
    _probeCts?.Cancel();
    _probeCts?.Dispose();
    _probeCts = new CancellationTokenSource();

    SetStatus("Validating…", ConfigStatusTone.Neutral); // 1. sync "working" state…
    RequestRedraw();                                     //    …painted on the loop thread

    _probeTask = RunProbeAsync(_probeCts.Token);         // 2. fire-and-forget, TRACKED
}

private async Task RunProbeAsync(CancellationToken ct)
{
    Result result;
    try { result = await _probe.ProbeAsync(ct); }        // 3. await OFF-loop (thread pool)
    catch (OperationCanceledException) { return; }       //    superseded/abandoned → drop

    if (ct.IsCancellationRequested) return;              // 4. re-check before publishing
                                                         //    (a stale result must not clobber)
    PublishProbeResult(result);                          // 5. synchronized publish; see below
    RequestRedraw();                                     // 6. schedule render. NEVER navigate here.
}

// Tests await this instead of Task.Delay / Thread.Sleep:
internal Task? PendingProbe => _probeTask;
```

The rules baked into that shape:

- **Track the task in a field.** Fire-and-forget is fine, *untracked* is not — you
  need it to cancel-and-await before a save (below) and to expose it to tests.
- **Own a `CancellationTokenSource`;** on restart, `Cancel()`+`Dispose()` the old
  one. Re-check `ct.IsCancellationRequested` *after* the await, before you publish —
  this is what stops a superseded probe from overwriting fresh state.
- **The continuation may only publish through a synchronized boundary and call
  `RequestRedraw()`. It must NEVER navigate, change focus, invalidate layout nodes,
  or set `ReactiveProperty` values with UI-mutating subscribers** off the loop.
- **If the published value is read by render/input, synchronize it.** Use a `lock`
  around a mutable collection plus a snapshot method (copy `HealthCheckStepViewModel`
  / `HealthCheckRunner`), replace the whole value with an immutable object, or use
  `Volatile`/`Interlocked` for simple scalar state.
- **Do not assume `RequestRedraw()` orders every later read.** Even if the channel
  enqueue/dequeue gives the redraw event an ordering edge, input events, timer
  invalidations, existing subscriptions, and current renders can read the same state
  outside that edge.
- **Expose the `Task`** (`PendingProbe`) so tests await it deterministically. No
  `Task.Delay`/`Thread.Sleep` in tests (see CLAUDE.md Testing Guidelines).

## The save-vs-background-write discipline

When a background task can **write the same state** a save reads (e.g. the label
refresh normalizes names->ids and persists), the save must cancel-and-await it
first so it can't land a stale snapshot over the fresh save:

```csharp
private async Task CancelAndAwaitLabelRefreshAsync()
{
    _labelResolutionCts?.Cancel();
    var inFlight = _labelRefreshTask;
    if (inFlight is null) return;
    await inFlight;            // the refresh swallows its own exceptions
    _labelRefreshTask = null;
}
// SaveAsync awaits this at its top, in an async method — NOT via .GetResult().
```

Keep the *consumer* async too: the save path is an `async Task`, dispatched
fire-and-forget from the handler (`_ = ViewModel.SaveFromInputAsync();`) or via
`ConfigAutosave.RunAsync`. Do **not** re-block it with `.GetAwaiter().GetResult()`.

This rule solves stale-writer ordering. It does **not** make the background task's
ordinary field writes safe while render/input can read them concurrently. Those
fields still need locks, immutable replacement, atomics, or loop-owned mutation.

## Streaming (the chat reference)

`netclaw chat` is the proof that async-to-front-end works. The daemon's
server-side `IAsyncEnumerable<token>` arrives over SignalR as a callback push that
is mapped onto an R3 `Subject`, and the page subscribes and appends:

- `DaemonClient.cs:78` — `_connection.On<…>("ReceiveOutput", dto => _outputSubject.OnNext(...))`
- `DaemonClient.cs:153` — `public Observable<SessionOutput> SessionOutput => _outputSubject.AsObservable();`
- `ChatPage.cs:78` — subscribe in `OnBound`; `ChatPage.cs:394-402` — append the delta to the
  `StreamingTextNode`; `ChatPage.cs:493` — `RequestRedraw()`.

Do not generalize this into "any off-loop mutation is fine." Chat streaming is a
dedicated push path whose page owns the append/redraw behavior. Before copying it,
verify the target node or subscriber is thread-safe, or publish into synchronized
state that the loop snapshots during render.

## Publication patterns that are safe on ARM64

### Locked mutable collection + snapshot

Use this when a background task appends or replaces items and the render path
enumerates them.

```csharp
private readonly List<HealthCheckItem> _results = [];

private void AddResult(HealthCheckItem item)
{
    lock (_results)
        _results.Add(item);
    RequestRedraw();
}

internal IReadOnlyList<HealthCheckItem> ResultsSnapshot()
{
    lock (_results)
        return _results.ToArray();
}
```

All readers and writers must use the same lock. Do not expose the mutable list as
the render surface unless callers are required to take the same lock.

### Immutable replacement

Use this when the background result is a complete value, not an incremental edit.
Build the value off-loop, then publish one immutable object/array. If the value is
read without a lock from another thread, publish/read via `Volatile` or another
explicit synchronization edge.

```csharp
private ImmutableArray<Row> _rows = [];

private void PublishRows(ImmutableArray<Row> rows)
{
    Volatile.Write(ref _rows, rows);
    RequestRedraw();
}

internal ImmutableArray<Row> RowsSnapshot() => Volatile.Read(ref _rows);
```

### Atomic scalar state

Use `Interlocked` for counters and task/CTS ownership; use `Volatile` for simple
single-writer flags. Never use `x++` on a cross-thread reactive version counter.

```csharp
private int _version;

private void PublishChanged()
{
    Interlocked.Increment(ref _version);
    RequestRedraw();
}

internal int Version => Volatile.Read(ref _version);
```

If a `ReactiveProperty<int>` is used only to wake page subscriptions, remember
that `.Value++` synchronously runs those subscriptions on the publishing thread.
Prefer a loop-owned invalidation path or a plain atomic version read by render.

## Current audit flags

These are not all necessarily bugs, but they are the fields/patterns that must be
checked before further TUI async work is considered safe:

- `HealthCheckStepViewModel`: `Results` is lock-synchronized; keep using
  `ResultsSnapshot()`. `ResultVersion`, `IsRunning`, `IsComplete`, `Succeeded`,
  `_context.StatusMessage`, and `LaunchChat()` are written from async health-check
  continuations and should not synchronously drive Termina invalidation/navigation
  off-loop.
- `ChannelsConfigViewModel`: `RefreshChannelLabelsAsync` / `ReconcileResolvedChannels`
  mutate `Step`, `_channelAudiences`, `Status`, `IsSaved`, and persisted config off-loop;
  page callbacks invalidate nodes inline. Either move reconciliation onto a loop-owned
  action or protect the shared state with a documented lock/snapshot discipline.
- `SkillSourcesConfigViewModel`: `RunProbeAsync` publishes `_pendingRemoteProbeResult`,
  `_pendingRemoteProbeMessage`, `Status`, and `IsSaved` from a background continuation;
  page subscriptions invalidate inline. Dispose cancels but does not drain `_probeTask`.
- `ProviderManagerViewModel`: eager probes mutate `DisplayProviders` rows and reactive
  state from background continuations; `StateVersion.Value++` drives inline invalidation;
  `_probeCts` ownership should use the `Interlocked.CompareExchange` pattern from
  `ProviderStepViewModel` to avoid one probe disposing a newer probe's CTS.
- `ExposureModeStepViewModel`: currently appears loop-owned; do not add background
  readers/writers without one of the publication strategies above.

Tests for these paths must be bounded. Do not use an unbounded writer loop plus a
large snapshot loop; that creates a CPU/memory stress test instead of a race test.
Use finite handshakes, cancel in `finally`, and `WaitAsync` when awaiting background
writers.

## Spinners and timers: let the node animate itself

Do **not** hand-roll a frame ticker. `SpinnerNode` (via `SpinnerViews`) owns its
own animation timer and bubbles invalidation up the layout tree; `ReactivePage`
subscribes the root node's `Invalidated` and calls `RequestRedraw()` for you. A
hand-rolled spinner tick field is the bug from #1312. For a live elapsed counter,
copy `ElapsedTimeSegment` (an `IAnimatedTextSegment` whose timer fires
`Invalidated.OnNext`). See `src/Netclaw.Cli/Tui/SpinnerViews.cs:16-24`.

## Anti-pattern: `.GetAwaiter().GetResult()` on the loop thread

This **freezes input and rendering** for the whole operation. The "it can't
deadlock because there's no SyncContext" argument is a red herring — no-deadlock
is not the same as non-blocking. Every network-bound `GetResult()` on the loop is
a bug to fix, not a pattern to copy.

If you find an old sync bridge in a TUI network/disk path, migrate it to the
tracked-task shape above. A bounded synchronous wait during disposal is a teardown
backstop, not an event-loop interaction pattern.

## Checklist before you write TUI async code

- [ ] Am I about to type `.GetAwaiter().GetResult()`? Stop. Use the tracked-task pattern.
- [ ] Is the network/disk await off-loop, with only the sync "working" setup on-loop?
- [ ] Owned CTS, cancelled+disposed on restart, re-checked after the await?
- [ ] Continuation publishes through a lock/immutable/atomic/loop-owned boundary, not plain fields?
- [ ] No off-loop `ReactiveProperty.Value` update has subscribers that touch Termina nodes?
- [ ] `RequestRedraw()` is used only to schedule a render, not as the only synchronization mechanism?
- [ ] No off-loop navigation, focus change, or `DynamicLayoutNode.Invalidate()`?
- [ ] Background task tracked in a field, exposed as `PendingX` for deterministic tests?
- [ ] Does any save read state this task writes? If so, cancel-and-await it before the save.

## Key reference files

- `src/Netclaw.Cli/Tui/Config/SkillSourcesConfigViewModel.cs` — useful probe shape, but audit its off-loop publication before copying
- `src/Netclaw.Cli/Tui/Config/ChannelsConfigViewModel.cs` — label-refresh/save ordering; do not copy its off-loop mutable-state publication without fixing synchronization
- `src/Netclaw.Cli/Tui/Wizard/Steps/ProviderStepViewModel.cs` — probe + cosmetic timer (`StartProbe`, `:155-244`)
- `src/Netclaw.Cli/Tui/Wizard/Steps/HealthCheckStepViewModel.cs` — streaming results into a locked list + version-counter redraw
- `src/Netclaw.Cli/Tui/ChatPage.cs` / `ChatViewModel.cs` / `Daemon/DaemonClient.cs` — live streaming to the front end
- `src/Netclaw.Cli/Tui/SpinnerViews.cs` — self-animating spinner (don't hand-roll)
